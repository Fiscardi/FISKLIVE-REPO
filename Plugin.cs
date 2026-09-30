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
                case "give_weapon":
                    GiveWeapon(ExtractValue(json, "weapon"));
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

            // Actualizado tras analizar un mod de referencia ya instalado:
            // el juego NO tiene una clase simple "PlayerHealth" - maneja la
            // vida como parte de una familia de componentes "UpgradePlayerX"
            // (UpgradePlayerHealth, UpgradePlayerEnergy, etc). Probamos
            // varios nombres candidatos en orden.
            string[] candidateTypeNames = { "PlayerHealth", "UpgradePlayerHealth" };
            object healthComponent = null;
            foreach (var typeName in candidateTypeNames)
            {
                healthComponent = TryGetComponentByTypeName(player, typeName);
                if (healthComponent != null) break;
            }
            healthComponent ??= TryGetFieldOrProperty(player, "playerHealth");

            if (healthComponent == null)
            {
                Log.LogError("No se encontro ningun componente de vida (probamos PlayerHealth, UpgradePlayerHealth, campo playerHealth). Revisar con el juego abierto que otros nombres puede tener.");
                return;
            }

            bool ok = TryInvoke(healthComponent, "Heal", new object[] { amount, false });
            if (!ok) Log.LogError($"Se encontro el componente ({healthComponent.GetType().Name}) pero no se pudo invocar Heal(int, bool). Puede que la firma real sea distinta (revisar el log completo del error mas arriba).");
            else Log.LogInfo($"Vida del jugador ajustada en {amount} via {healthComponent.GetType().Name}");
        }

        private void ToggleGodMode()
        {
            var player = FindLocalPlayer();
            if (player == null) return;

            ApplyHealthDelta(9999);

            // EnergyCurrent esta confirmado como campo real (visto en un mod
            // de referencia ya instalado). Lo llenamos tambien via reflection,
            // buscandolo directo en PlayerAvatar o en UpgradePlayerEnergy.
            object energyHolder = TryGetComponentByTypeName(player, "UpgradePlayerEnergy") ?? player;
            var field = energyHolder?.GetType().GetField("EnergyCurrent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(energyHolder, 100f);
                Log.LogInfo("EnergyCurrent seteado a 100");
            }
            else
            {
                Log.LogWarning("No se encontro el campo EnergyCurrent en PlayerAvatar ni en UpgradePlayerEnergy.");
            }
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

            // NOTA: un mod de referencia ya instalado tiene una clase propia
            // llamada "TeleportPlayerPatch" (Harmony) para esto - es señal de
            // que mover el Transform a mano puede no alcanzar (el juego usa
            // CharacterController y puede "pisar" la posicion cada frame).
            // Si esto no se nota en el juego, el siguiente paso es agregar
            // HarmonyLib como dependencia y patchear en vez de asignar
            // directo, en lugar de seguir peleando con el Transform.
            Vector3 offset = new Vector3(UnityEngine.Random.Range(-10f, 10f), 0f, UnityEngine.Random.Range(-10f, 10f));
            transform.position += offset;
            Log.LogInfo($"Jugador teletransportado con offset {offset} (si no se nota nada, revisar nota de Harmony en el codigo)");
        }

        // Spawn de enemigos via REPOLib.Modules.Enemies (confirmado por el
        // escaneo: tiene TryGetEnemyThatContainsName, AllEnemies y SpawnEnemy).
        // Se usa por reflection para no depender de referenciar el DLL de REPOLib.
        private static List<UnityEngine.Object> GetEnemyCatalog(Type enemiesType)
        {
            var list = new List<UnityEngine.Object>();
            var prop = enemiesType.GetProperty("AllEnemies", BindingFlags.Public | BindingFlags.Static);
            if (prop?.GetValue(null, null) is System.Collections.IEnumerable en)
            {
                foreach (var item in en)
                {
                    if (item is UnityEngine.Object o) list.Add(o);
                }
            }
            return list;
        }

        private static object GetStaticProp(Type t, string name)
        {
            try { return t?.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null); }
            catch { return "?"; }
        }

        private void SpawnEnemy(string enemyName)
        {
            Type enemiesType = FindGameType("REPOLib.Modules.Enemies");
            if (enemiesType == null)
            {
                Log.LogError("spawn_enemy: no se encontro REPOLib.Modules.Enemies (¿REPOLib esta instalado y cargado?).");
                return;
            }

            // Diagnostico: estado de Photon. No bloqueamos nada: si no sos el
            // host, REPOLib/el juego lo va a rechazar y lo vemos en el log.
            Type photonType = FindGameType("PhotonNetwork");
            Log.LogInfo($"spawn_enemy: Photon -> IsMasterClient={GetStaticProp(photonType, "IsMasterClient")}, InRoom={GetStaticProp(photonType, "InRoom")}, OfflineMode={GetStaticProp(photonType, "OfflineMode")}");

            // 1) Elegir el EnemySetup
            UnityEngine.Object setup = null;
            var catalog = GetEnemyCatalog(enemiesType);

            if (string.IsNullOrEmpty(enemyName))
            {
                if (catalog.Count > 0) setup = catalog[UnityEngine.Random.Range(0, catalog.Count)];
            }
            else
            {
                var tryGet = enemiesType.GetMethod("TryGetEnemyThatContainsName", BindingFlags.Public | BindingFlags.Static);
                if (tryGet != null)
                {
                    var args = new object[] { enemyName, null };
                    bool found = (bool)tryGet.Invoke(null, args);
                    if (found) setup = args[1] as UnityEngine.Object;
                }
            }

            if (setup == null)
            {
                Log.LogWarning($"spawn_enemy: no se encontro ningun enemigo que contenga '{enemyName ?? "(al azar)"}'. Enemigos disponibles ({catalog.Count}):");
                foreach (var o in catalog) Log.LogWarning($"  - {o.name}");
                return;
            }

            // 2) Posicion: 4 metros adelante del jugador
            var player = FindLocalPlayer();
            var playerTransform = player != null ? TryGetTransform(player) : null;
            Vector3 spawnPos = playerTransform != null
                ? playerTransform.position + playerTransform.forward * 4f + Vector3.up * 0.2f
                : Vector3.zero;

            // 3) Spawnear: SpawnEnemy(EnemySetup, Vector3, Quaternion, bool spawnDespawned)
            var spawn = enemiesType.GetMethod("SpawnEnemy", BindingFlags.Public | BindingFlags.Static);
            if (spawn == null)
            {
                Log.LogError("spawn_enemy: REPOLib.Modules.Enemies no tiene el metodo SpawnEnemy.");
                return;
            }

            try
            {
                object result = spawn.Invoke(null, new object[] { setup, spawnPos, Quaternion.identity, false });
                int count = result is System.Collections.ICollection col ? col.Count : -1;
                Log.LogInfo($"spawn_enemy: '{setup.name}' spawneado en {spawnPos} (objetos devueltos: {count}).");
            }
            catch (Exception ex)
            {
                Log.LogError($"spawn_enemy: fallo al spawnear '{setup.name}': {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        private void GiveWeapon(string weaponName)
        {
            var player = FindLocalPlayer();
            var playerTransform = player != null ? TryGetTransform(player) : null;
            if (playerTransform == null)
            {
                Log.LogError("give_weapon: no se encontro el transform del jugador.");
                return;
            }

            // Por lo que se ve en las clases del juego (PhysGrabObject,
            // ItemToggle, etc), las armas/items aca no son un "inventario"
            // clasico: son objetos fisicos que el jugador agarra. Por eso,
            // en vez de "dar" el arma directamente a una lista interna, la
            // spawneamos justo enfrente del jugador para que la levante.
            Vector3 spawnPos = playerTransform.position + playerTransform.forward * 1.5f + Vector3.up * 0.5f;
            string prefabName = string.IsNullOrEmpty(weaponName) ? "Items/Gun" : $"Items/{weaponName}";

            bool ok = TryInvokeStatic("PhotonNetwork", "Instantiate",
                new object[] { prefabName, spawnPos, Quaternion.identity }, out _);

            if (ok)
            {
                Log.LogInfo($"give_weapon: PhotonNetwork.Instantiate('{prefabName}') se llamo sin excepcion, deberia aparecer justo enfrente tuyo.");
            }
            else
            {
                Log.LogWarning($"give_weapon: no se pudo instanciar '{prefabName}'. Hay que confirmar el nombre real del prefab del arma dentro de Resources (probar sin el prefijo 'Items/', u otro nombre de carpeta).");
            }
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

        // Para llamar metodos ESTATICOS (como PhotonNetwork.Instantiate, que
        // no es sobre una instancia de nada, se llama directo en la clase).
        private static bool TryInvokeStatic(string typeName, string methodName, object[] args, out object result)
        {
            result = null;
            Type t = FindGameType(typeName);
            if (t == null)
            {
                Log.LogError($"No se encontro el tipo '{typeName}' (¿PUN esta cargado? ¿el nombre de namespace es otro?).");
                return false;
            }

            // Antes exigiamos coincidencia EXACTA de cantidad de parametros,
            // pero metodos como PhotonNetwork.Instantiate suelen tener MAS
            // parametros de los que mandamos, con los ultimos opcionales
            // (con valor por defecto). Ahora aceptamos cualquier metodo con
            // al menos tantos parametros como argumentos mandamos, siempre
            // que el resto tenga un default declarado.
            var candidates = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name == methodName && m.GetParameters().Length >= args.Length)
                .OrderBy(m => m.GetParameters().Length);

            foreach (var method in candidates)
            {
                var parameters = method.GetParameters();
                bool restAreOptional = true;
                for (int i = args.Length; i < parameters.Length; i++)
                {
                    if (!parameters[i].HasDefaultValue) { restAreOptional = false; break; }
                }
                if (!restAreOptional) continue;

                var fullArgs = new object[parameters.Length];
                Array.Copy(args, fullArgs, args.Length);
                for (int i = args.Length; i < parameters.Length; i++)
                {
                    fullArgs[i] = parameters[i].DefaultValue;
                }

                string sig = string.Join(", ", parameters.Select(p => p.ParameterType.Name));
                Log.LogInfo($"Probando {typeName}.{methodName}({sig})");

                try
                {
                    result = method.Invoke(null, fullArgs);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.LogError($"Invoke estatico de {typeName}.{methodName} fallo: {ex.InnerException?.Message ?? ex.Message}");
                    return false;
                }
            }

            Log.LogError($"'{typeName}' no tiene ningun metodo estatico '{methodName}' compatible con {args.Length} argumentos (probando tambien con parametros extra opcionales).");
            return false;
        }
    }
}
