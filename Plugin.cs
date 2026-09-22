using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace FiskLiveREPO
{
    // ----------------------------------------------------------------------
    // FiskLiveREPO: mod de R.E.P.O. para recibir comandos desde la app
    // FiskLive por TCP, mismo patron que FiskLiveGTA.cs (una conexion por
    // comando, JSON simple con un campo "action").
    //
    // IMPORTANTE - LEER ANTES DE COMPILAR:
    // Las acciones estan separadas en dos grupos, marcados en el codigo:
    //
    //   [CONFIABLE] -> usan solo UnityEngine puro (Time, Physics, Light).
    //   Estas son API estandar de Unity, no dependen de nada especifico de
    //   R.E.P.O., asi que deberian compilar y funcionar sin cambios.
    //
    //   [NECESITA AJUSTE] -> tocan clases internas de R.E.P.O. (PlayerAvatar,
    //   PlayerHealth, EnemyDirector, etc). No tengo forma de abrir el DLL
    //   real del juego desde este entorno para confirmar los nombres EXACTOS
    //   de metodos/propiedades, asi que estas estan escritas con reflection
    //   (buscan la clase/metodo por STRING en vez de llamarla directo).
    //   Esto es a proposito: si el nombre esta mal, falla en tiempo de
    //   ejecucion con un error que se ve en el log (y se puede corregir sin
    //   romper la compilacion de todo el mod), en vez de que el proyecto
    //   entero no compile. Cuando probemos esto en el juego de verdad, vamos
    //   a tener que ajustar los strings de tipo/metodo segun lo que diga el
    //   log real - es esperable, no significa que algo este "mal armado".
    // ----------------------------------------------------------------------

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.fisklive.repo";
        public const string PluginName = "FiskLiveREPO";
        public const string PluginVersion = "1.0.0";

        private const int ListenPort = 8422; // GTA usa 8421, dejamos este libre
        private static ManualLogSource Log;

        private TcpListener _listener;
        private Thread _listenerThread;
        private volatile bool _running;
        private readonly ConcurrentQueue<string> _commandQueue = new ConcurrentQueue<string>();

        // Estado de efectos con duracion, para poder revertirlos solos
        private float? _timeScaleRevertAt;
        private float _originalTimeScale = 1f;
        private float? _blackoutRevertAt;
        private List<Light> _blackoutLights;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginName} cargado, escuchando comandos en el puerto {ListenPort}");

            _running = true;
            _listenerThread = new Thread(ListenLoop) { IsBackground = true };
            _listenerThread.Start();
        }

        private void OnDestroy()
        {
            _running = false;
            try { _listener?.Stop(); } catch { /* noop */ }
        }

        // ---------- Servidor TCP (igual patron que gtaConnector.js/FiskLiveGTA.cs) ----------

        private void ListenLoop()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, ListenPort);
                _listener.Start();

                while (_running)
                {
                    TcpClient client;
                    try
                    {
                        client = _listener.AcceptTcpClient();
                    }
                    catch
                    {
                        break; // el listener se cerro (OnDestroy)
                    }

                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"No se pudo abrir el puerto {ListenPort}: {ex.Message}");
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[4096];
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) return;

                    string json = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                    _commandQueue.Enqueue(json);

                    byte[] ok = Encoding.UTF8.GetBytes("OK\n");
                    stream.Write(ok, 0, ok.Length);
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Conexion de comando fallo: {ex.Message}");
            }
        }

        // Unity solo permite tocar la mayoria de sus APIs desde el hilo
        // principal, por eso los comandos se encolan en HandleClient (que
        // corre en un hilo aparte) y se procesan aca, en Update().
        private void Update()
        {
            while (_commandQueue.TryDequeue(out string json))
            {
                try
                {
                    HandleCommand(json);
                }
                catch (Exception ex)
                {
                    Log.LogError($"Error procesando comando: {ex.Message}\n{ex.StackTrace}");
                }
            }

            if (_timeScaleRevertAt.HasValue && Time.unscaledTime >= _timeScaleRevertAt.Value)
            {
                Time.timeScale = _originalTimeScale;
                _timeScaleRevertAt = null;
            }

            if (_blackoutRevertAt.HasValue && Time.unscaledTime >= _blackoutRevertAt.Value)
            {
                RestoreLighting();
            }
        }

        // ---------- Parseo de JSON (mismo estilo simple que FiskLiveGTA.cs) ----------

        private static string ExtractValue(string json, string key)
        {
            var match = Regex.Match(json, $"\"{key}\"\\s*:\\s*\"([^\"]*)\"");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static int ExtractInt(string json, string key, int fallback)
        {
            var match = Regex.Match(json, $"\"{key}\"\\s*:\\s*(-?\\d+)");
            return match.Success ? int.Parse(match.Groups[1].Value) : fallback;
        }

        private static float ExtractFloat(string json, string key, float fallback)
        {
            var match = Regex.Match(json, $"\"{key}\"\\s*:\\s*(-?\\d+(\\.\\d+)?)");
            return match.Success ? float.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : fallback;
        }

        // ---------- Dispatch de comandos ----------

        private void HandleCommand(string json)
        {
            string action = ExtractValue(json, "action");
            Log.LogInfo($"Comando recibido: {action}");

            switch (action)
            {
                // [CONFIABLE]
                case "time_scale":
                    SetTimeScale(ExtractFloat(json, "scale", 0.3f), ExtractInt(json, "seconds", 10));
                    break;

                // [CONFIABLE]
                case "freeze_world_physics":
                    SetTimeScale(0f, ExtractInt(json, "seconds", 5));
                    break;

                // [CONFIABLE]
                case "gravity_set":
                    SetGravity(ExtractValue(json, "preset") ?? "invertida", ExtractInt(json, "seconds", 15));
                    break;

                // [CONFIABLE]
                case "blackout":
                    Blackout(ExtractInt(json, "seconds", 10));
                    break;

                // [CONFIABLE]
                case "restore_lighting":
                    RestoreLighting();
                    break;

                // [NECESITA AJUSTE]
                case "heal_player":
                    ApplyHealthDelta(ExtractInt(json, "amount", 20));
                    break;

                // [NECESITA AJUSTE]
                case "damage_player":
                    ApplyHealthDelta(-ExtractInt(json, "amount", 20));
                    break;

                // [NECESITA AJUSTE]
                case "god_mode_toggle":
                    ToggleGodMode();
                    break;

                // [NECESITA AJUSTE]
                case "teleport_random":
                    TeleportPlayerRandom();
                    break;

                // [NECESITA AJUSTE]
                case "spawn_enemy":
                    SpawnEnemy(ExtractValue(json, "enemy"));
                    break;

                // [NECESITA AJUSTE]
                case "enemy_horde":
                    int count = ExtractInt(json, "count", 4);
                    for (int i = 0; i < count; i++) SpawnEnemy(null);
                    break;

                // [NECESITA AJUSTE]
                case "freeze_enemies":
                    SetEnemiesFrozen(true);
                    break;

                // [NECESITA AJUSTE]
                case "unfreeze_enemies":
                    SetEnemiesFrozen(false);
                    break;

                // [NECESITA AJUSTE]
                case "spawn_valuable":
                    SpawnViaDevCommand("spawnvaluable", ExtractValue(json, "name") ?? "diamond");
                    break;

                // [NECESITA AJUSTE]
                case "spawn_item":
                    SpawnViaDevCommand("spawnitem", ExtractValue(json, "name") ?? "gun");
                    break;

                default:
                    Log.LogWarning($"Accion desconocida: {action}");
                    break;
            }
        }

        // ============================================================
        // [CONFIABLE] Acciones con UnityEngine puro
        // ============================================================

        private void SetTimeScale(float scale, int seconds)
        {
            if (!_timeScaleRevertAt.HasValue) _originalTimeScale = Time.timeScale;
            Time.timeScale = scale;
            _timeScaleRevertAt = Time.unscaledTime + seconds;
            Log.LogInfo($"Time.timeScale = {scale} por {seconds}s");
        }

        private void SetGravity(string preset, int seconds)
        {
            Vector3 original = Physics.gravity;
            Vector3 target;
            switch (preset.ToLowerInvariant())
            {
                case "invertida":
                    target = new Vector3(original.x, Mathf.Abs(original.y), original.z);
                    break;
                case "liviana":
                    target = original * 0.3f;
                    break;
                case "pesada":
                    target = original * 2.5f;
                    break;
                default:
                    target = original;
                    break;
            }

            Physics.gravity = target;
            Log.LogInfo($"Physics.gravity = {target} (preset {preset}) por {seconds}s");

            // Revertir despues de X segundos. Usamos una corutina simple
            // via Invoke en vez de sumar otro campo de estado.
            Invoke(nameof(NoOp), 0f); // asegura que el componente siga "vivo" para Invoke
            StartCoroutineRevertGravity(original, seconds);
        }

        private void NoOp() { }

        private void StartCoroutineRevertGravity(Vector3 original, int seconds)
        {
            StartCoroutine(RevertGravityAfter(original, seconds));
        }

        private System.Collections.IEnumerator RevertGravityAfter(Vector3 original, int seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            Physics.gravity = original;
            Log.LogInfo("Gravedad restaurada");
        }

        private void Blackout(int seconds)
        {
            _blackoutLights = GameObject.FindObjectsOfType<Light>().ToList();
            foreach (var light in _blackoutLights) light.enabled = false;
            RenderSettings.ambientIntensity = 0f;
            _blackoutRevertAt = Time.unscaledTime + seconds;
            Log.LogInfo($"Blackout: {_blackoutLights.Count} luces apagadas por {seconds}s");
        }

        private void RestoreLighting()
        {
            if (_blackoutLights != null)
            {
                foreach (var light in _blackoutLights)
                {
                    if (light != null) light.enabled = true;
                }
            }
            RenderSettings.ambientIntensity = 1f;
            _blackoutRevertAt = null;
            Log.LogInfo("Luces restauradas");
        }

        // ============================================================
        // [NECESITA AJUSTE] Acciones que dependen de clases de R.E.P.O.
        // Usan reflection a proposito (ver nota al principio del archivo).
        // ============================================================

        // Busca un tipo por nombre en todos los assemblies cargados
        // (incluye Assembly-CSharp, que es donde vive el codigo del juego).
        private static Type FindGameType(string typeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type direct = null;
                try { direct = asm.GetType(typeName); } catch { /* ignore */ }
                if (direct != null) return direct;

                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == typeName) return t;
                    }
                }
                catch { /* algunos assemblies tiran ReflectionTypeLoadException, los saltamos */ }
            }
            return null;
        }

        private static UnityEngine.Object FindFirstInstance(string typeName)
        {
            Type t = FindGameType(typeName);
            if (t == null) return null;
            var results = Resources.FindObjectsOfTypeAll(t);
            return results != null && results.Length > 0 ? results[0] as UnityEngine.Object : null;
        }

        // Intenta encontrar el jugador local. "PlayerAvatar" es el nombre de
        // clase que aparece en varios mods publicos de R.E.P.O. (REPO_UTILS),
        // pero puede que haya que usar una instancia especifica (la del
        // jugador LOCAL, no cualquiera) - eso es lo primero a revisar si
        // esto no apunta al jugador correcto en multiplayer.
        private UnityEngine.Object FindLocalPlayer()
        {
            var player = FindFirstInstance("PlayerAvatar");
            if (player == null)
            {
                Log.LogError("No se encontro la clase 'PlayerAvatar'. Puede que el nombre real sea distinto - revisar con dnSpy/ILSpy sobre el DLL del juego.");
            }
            return player;
        }

        private void ApplyHealthDelta(int amount)
        {
            var player = FindLocalPlayer();
            if (player == null) return;

            // PlayerHealth.Heal(int, bool) esta confirmado en el codigo
            // publico de REPO_UTILS. Puede estar como componente separado
            // (GetComponent) o como campo/propiedad de PlayerAvatar - probamos
            // ambos caminos.
            object healthComponent = TryGetComponentByTypeName(player, "PlayerHealth")
                                      ?? TryGetFieldOrProperty(player, "playerHealth");

            if (healthComponent == null)
            {
                Log.LogError("No se encontro 'PlayerHealth' en el jugador (ni como componente ni como campo 'playerHealth'). Ajustar segun el log del juego.");
                return;
            }

            bool ok = TryInvoke(healthComponent, "Heal", new object[] { amount, false });
            if (!ok) Log.LogError("No se pudo invocar Heal(int, bool) en PlayerHealth. Puede que la firma real sea distinta.");
            else Log.LogInfo($"Vida del jugador ajustada en {amount}");
        }

        private void ToggleGodMode()
        {
            var player = FindLocalPlayer();
            if (player == null) return;

            // REPO_UTILS implementa "God Mode" combinando varios campos
            // (SprintSpeed, EnergyCurrent) en vez de un solo booleano.
            // Como primer paso simple, curamos a full y dejamos un aviso:
            // esto probablemente haya que expandirlo con mas campos una vez
            // que probemos en el juego real.
            ApplyHealthDelta(9999);
            Log.LogWarning("god_mode_toggle: por ahora solo cura al maximo. Falta implementar invencibilidad real (revisar SprintSpeed/EnergyCurrent en PlayerAvatar).");
        }

        private void TeleportPlayerRandom()
        {
            var player = FindLocalPlayer();
            if (player == null) return;

            var transform = TryGetTransform(player);
            if (transform == null)
            {
                Log.LogError("No se pudo obtener el Transform del jugador.");
                return;
            }

            Vector3 offset = new Vector3(UnityEngine.Random.Range(-10f, 10f), 0f, UnityEngine.Random.Range(-10f, 10f));
            transform.position += offset;
            Log.LogInfo($"Jugador teletransportado con offset {offset}");
        }

        private void SpawnEnemy(string enemyName)
        {
            // Los mods existentes (Enemy Spawner, EnemySpawning) confirman
            // que esto es posible via EnemyDirector + PhotonNetwork.Instantiate,
            // pero no tengo los nombres EXACTOS de metodo. Dejamos el intento
            // con reflection: si falla, el log va a decir exactamente que
            // metodo no encontro, y ahi ajustamos.
            Type enemyDirectorType = FindGameType("EnemyDirector");
            if (enemyDirectorType == null)
            {
                Log.LogError("No se encontro la clase 'EnemyDirector'. spawn_enemy no implementado todavia.");
                return;
            }

            var director = FindFirstInstance("EnemyDirector");
            if (director == null)
            {
                Log.LogError("Existe la clase EnemyDirector pero no hay ninguna instancia activa en la escena (¿estamos en una partida?).");
                return;
            }

            Log.LogWarning($"spawn_enemy: se encontro EnemyDirector pero todavia no se confirmo el metodo exacto para spawnear '{enemyName ?? "random"}'. Pendiente de ajuste con el juego abierto.");
        }

        private void SetEnemiesFrozen(bool frozen)
        {
            Log.LogWarning($"freeze_enemies({frozen}): pendiente de implementar - necesita confirmar la clase de IA/movimiento de los enemigos (revisar con el juego abierto).");
        }

        // REPOLib trae comandos de chat como /spawnvaluable y /spawnitem
        // (solo en modo desarrollador, solo host, solo en multiplayer). Como
        // alternativa mas simple que reimplementar el spawn a mano, se puede
        // simular el envio de ese comando de chat. Placeholder por ahora.
        private void SpawnViaDevCommand(string command, string args)
        {
            Log.LogWarning($"spawn via '/{command} {args}': pendiente - requiere DeveloperMode activado y probar como inyectar el comando de chat sin que un jugador lo tipee a mano.");
        }

        // ---------- Helpers de reflection ----------

        private static object TryGetComponentByTypeName(UnityEngine.Object obj, string typeName)
        {
            var component = obj as Component;
            if (component == null) return null;

            Type t = FindGameType(typeName);
            if (t == null) return null;

            return component.GetComponent(t);
        }

        private static object TryGetFieldOrProperty(object obj, string name)
        {
            if (obj == null) return null;
            Type t = obj.GetType();

            var field = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) return field.GetValue(obj);

            var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null) return prop.GetValue(obj);

            return null;
        }

        private static Transform TryGetTransform(UnityEngine.Object obj)
        {
            if (obj is Component c) return c.transform;
            if (obj is GameObject g) return g.transform;
            return null;
        }

        private static bool TryInvoke(object target, string methodName, object[] args)
        {
            if (target == null) return false;
            Type t = target.GetType();
            var method = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null) return false;

            try
            {
                method.Invoke(target, args);
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError($"Invoke de {methodName} fallo: {ex.Message}");
                return false;
            }
        }
    }
}
