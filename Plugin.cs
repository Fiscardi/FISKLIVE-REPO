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

            // R.E.P.O. (como otros juegos Unity) puede destruir el GameObject del
            // plugin al cargar escenas. Cuando eso pasa se llama OnDestroy, que
            // cierra el puerto. Esto lo protege para que el plugin sobreviva.
            gameObject.transform.parent = null;
            gameObject.hideFlags = HideFlags.HideAndDontSave;

            Log.LogInfo($"{PluginName} cargado, iniciando servidor en el puerto {ListenPort}");

            _running = true;
            _listenerThread = new Thread(ListenLoop) { IsBackground = true };
            _listenerThread.Start();
        }

        private void OnDestroy()
        {
            // Si este mensaje aparece en el log, el juego destruyo el plugin.
            Log?.LogWarning("OnDestroy llamado: el plugin fue destruido, se cierra el puerto.");
            _running = false;
            try { _listener?.Stop(); } catch { /* noop */ }
        }

        // ---------- Servidor TCP (igual patron que gtaConnector.js/FiskLiveGTA.cs) ----------

        private void ListenLoop()
        {
            try
            {
                if (!_running)
                {
                    Log.LogWarning("ListenLoop: el plugin ya estaba destruido antes de abrir el puerto.");
                    return;
                }

                _listener = new TcpListener(IPAddress.Loopback, ListenPort);
                _listener.Start();
                Log.LogInfo($"Puerto abierto: escuchando en 127.0.0.1:{ListenPort}");

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
            finally
            {
                try { _listener?.Stop(); } catch { /* noop */ }
                Log.LogInfo("ListenLoop terminado (el puerto ya no esta abierto).");
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

                // Enemigos e items: via REPOLib (ver seccion "Spawn de enemigos e items")
                case "spawn_enemy":
                    SpawnEnemies(ExtractValue(json, "enemy"), 1);
                    break;

                case "enemy_horde":
                    SpawnEnemies(ExtractValue(json, "enemy"), ExtractInt(json, "count", 4));
                    break;

                case "list_enemies":
                    ListModuleNames("REPOLib.Modules.Enemies", "enemigos", "AllEnemies", "GetEnemies");
                    break;

                case "list_items":
                    ListModuleNames("REPOLib.Modules.Items", "items", "AllItems", "GetItems");
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

                case "spawn_item":
                    SpawnItems(ExtractValue(json, "name"), ExtractInt(json, "count", 1));
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

        // ============================================================
        // Spawn de enemigos e items (armas) via REPOLib, por reflection
        // ============================================================
        // REPOLib es un mod aparte (Thunderstore: Zehs-REPOLib) que tiene que
        // estar instalado en BepInEx\plugins. Su API sabe spawnear enemigos e
        // items de forma segura, tambien en singleplayer. Lo llamamos por
        // reflection para que este mod cargue igual aunque REPOLib no este:
        // en ese caso solo se loguea un error claro. Si algun nombre de
        // metodo no coincide, el log muestra los metodos que si existen.

        private static Type FindRepoLibType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName;
                try { asmName = asm.GetName().Name; } catch { continue; }
                if (!string.Equals(asmName, "REPOLib", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var t = asm.GetType(fullName);
                    if (t != null) return t;
                }
                catch { /* seguimos con el siguiente assembly */ }
            }
            return null;
        }

        // Lee una lista estatica (propiedad o metodo sin parametros) de un
        // modulo de REPOLib, probando varios nombres posibles en orden.
        private static List<UnityEngine.Object> GetAllFromModule(Type module, params string[] memberNames)
        {
            var result = new List<UnityEngine.Object>();
            if (module == null) return result;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            foreach (var memberName in memberNames)
            {
                object value = null;
                try
                {
                    var prop = module.GetProperty(memberName, flags);
                    if (prop != null)
                    {
                        value = prop.GetValue(null, null);
                    }
                    else
                    {
                        var method = module.GetMethods(flags)
                            .FirstOrDefault(m => m.Name == memberName && m.GetParameters().Length == 0);
                        if (method != null) value = method.Invoke(null, null);
                    }
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"No se pudo leer {module.Name}.{memberName}: {ex.Message}");
                }

                var enumerable = value as System.Collections.IEnumerable;
                if (enumerable == null) continue;

                foreach (var entry in enumerable)
                {
                    var unityObj = entry as UnityEngine.Object;
                    if (unityObj != null && !result.Contains(unityObj)) result.Add(unityObj);
                }
                if (result.Count > 0) break;
            }
            return result;
        }

        // Loguea los metodos publicos de un modulo, para diagnosticar cuando
        // la API de REPOLib no coincide con lo que esperamos.
        private static void DescribeModule(Type module)
        {
            if (module == null) return;
            var sb = new StringBuilder();
            sb.AppendLine($"Metodos publicos de {module.FullName}:");
            foreach (var m in module.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name).ToArray());
                sb.AppendLine($"  {m.ReturnType.Name} {m.Name}({ps})");
            }
            Log.LogWarning(sb.ToString());
        }

        // Elige por nombre: coincidencia exacta, o el nombre mas corto que
        // contenga el texto. Texto vacio o "random" = uno al azar.
        private static UnityEngine.Object PickByName(List<UnityEngine.Object> all, string term)
        {
            if (all == null || all.Count == 0) return null;
            if (string.IsNullOrEmpty(term) || term.Equals("random", StringComparison.OrdinalIgnoreCase))
                return all[UnityEngine.Random.Range(0, all.Count)];

            var exact = all.FirstOrDefault(o => o.name.Equals(term, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            return all
                .Where(o => o.name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(o => o.name.Length)
                .FirstOrDefault();
        }

        // Llama a un metodo estatico de REPOLib armando los argumentos segun
        // los tipos de sus parametros (asi aguanta pequenas diferencias de
        // firma, por ejemplo un parametro bool opcional de mas).
        private static object InvokeStatic(Type module, string methodName, params object[] candidates)
        {
            var methods = module.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == methodName)
                .OrderByDescending(m => m.GetParameters().Length)
                .ToList();

            foreach (var method in methods)
            {
                var ps = method.GetParameters();
                var args = new object[ps.Length];
                bool ok = true;

                for (int i = 0; i < ps.Length; i++)
                {
                    object match = null;
                    foreach (var candidate in candidates)
                    {
                        if (candidate != null && ps[i].ParameterType.IsInstanceOfType(candidate))
                        {
                            match = candidate;
                            break;
                        }
                    }

                    if (match != null) args[i] = match;
                    else if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
                    else { ok = false; break; }
                }

                if (!ok) continue;

                try
                {
                    return method.Invoke(null, args);
                }
                catch (TargetInvocationException tie)
                {
                    throw tie.InnerException ?? tie;
                }
            }

            throw new MissingMethodException($"{module.Name}.{methodName} no tiene ninguna sobrecarga compatible con los datos que tenemos.");
        }

        // Punto de spawn alrededor del jugador local. Si snapToGround, baja
        // hasta el piso con un raycast (los enemigos necesitan estar en el piso).
        private Vector3? FindSpawnPositionNearPlayer(float minDistance, float maxDistance, bool snapToGround)
        {
            object player = null;
            Type playerType = FindGameType("PlayerAvatar");
            if (playerType != null)
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                var field = playerType.GetField("instance", flags);
                if (field != null)
                {
                    player = field.GetValue(null);
                }
                else
                {
                    var prop = playerType.GetProperty("instance", flags);
                    if (prop != null) player = prop.GetValue(null, null);
                }
            }

            var component = player as Component;
            if (component == null) component = FindLocalPlayer() as Component;
            if (component == null) return null;

            Vector3 origin = component.transform.position;
            float angle = UnityEngine.Random.Range(0f, 360f);
            float distance = UnityEngine.Random.Range(minDistance, maxDistance);
            Vector3 pos = origin + (Quaternion.Euler(0f, angle, 0f) * Vector3.forward) * distance;
            pos += Vector3.up * 1f;

            if (snapToGround)
            {
                RaycastHit hit;
                if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out hit, 15f, ~0, QueryTriggerInteraction.Ignore))
                {
                    pos = hit.point + Vector3.up * 0.3f;
                }
            }
            return pos;
        }

        private void SpawnEnemies(string enemyName, int count)
        {
            Type module = FindRepoLibType("REPOLib.Modules.Enemies");
            if (module == null)
            {
                Log.LogError("No se encontro REPOLib. Para spawnear enemigos hay que instalar el mod Zehs-REPOLib (Thunderstore) en BepInEx\\plugins.");
                return;
            }

            var all = GetAllFromModule(module, "AllEnemies", "GetEnemies");
            if (all.Count == 0)
            {
                Log.LogError("REPOLib no devolvio ninguna lista de enemigos (¿estas dentro de una partida? o la API cambio).");
                DescribeModule(module);
                return;
            }

            count = Mathf.Clamp(count, 1, 20);
            for (int i = 0; i < count; i++)
            {
                var setup = PickByName(all, enemyName);
                if (setup == null)
                {
                    Log.LogWarning($"No hay ningun enemigo que coincida con '{enemyName}'. Disponibles: {string.Join(", ", all.Select(o => o.name).ToArray())}");
                    return;
                }

                var pos = FindSpawnPositionNearPlayer(6f, 12f, true);
                if (pos == null)
                {
                    Log.LogError("No se pudo calcular una posicion cerca del jugador.");
                    return;
                }

                try
                {
                    InvokeStatic(module, "SpawnEnemy", setup, pos.Value, Quaternion.identity, true);
                    Log.LogInfo($"Enemigo spawneado: {setup.name} en {pos.Value}");
                }
                catch (Exception ex)
                {
                    Log.LogError($"Fallo SpawnEnemy de REPOLib: {ex.Message}");
                    DescribeModule(module);
                    return;
                }
            }
        }

        // itemName: nombre (o parte del nombre) del item, "random" para uno al
        // azar, o "random_weapon" para un arma al azar (gun / melee / grenade / mine).
        private void SpawnItems(string itemName, int count)
        {
            Type module = FindRepoLibType("REPOLib.Modules.Items");
            if (module == null)
            {
                Log.LogError("No se encontro REPOLib. Para spawnear items hay que instalar el mod Zehs-REPOLib (Thunderstore) en BepInEx\\plugins.");
                return;
            }

            var all = GetAllFromModule(module, "AllItems", "GetItems");
            if (all.Count == 0)
            {
                Log.LogError("REPOLib no devolvio ninguna lista de items (¿estas dentro de una partida? o la API cambio).");
                DescribeModule(module);
                return;
            }

            var pool = all;
            string term = itemName;
            if (string.Equals(itemName, "random_weapon", StringComparison.OrdinalIgnoreCase))
            {
                string[] weaponWords = { "gun", "melee", "grenade", "mine" };
                pool = all
                    .Where(o => weaponWords.Any(w => o.name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                    .ToList();
                term = null;
                if (pool.Count == 0)
                {
                    Log.LogWarning($"No encontre items que parezcan armas. Items disponibles: {string.Join(", ", all.Select(o => o.name).ToArray())}");
                    return;
                }
            }

            count = Mathf.Clamp(count, 1, 20);
            for (int i = 0; i < count; i++)
            {
                var item = PickByName(pool, term);
                if (item == null)
                {
                    Log.LogWarning($"No hay ningun item que coincida con '{itemName}'. Disponibles: {string.Join(", ", all.Select(o => o.name).ToArray())}");
                    return;
                }

                var pos = FindSpawnPositionNearPlayer(2f, 4f, false);
                if (pos == null)
                {
                    Log.LogError("No se pudo calcular una posicion cerca del jugador.");
                    return;
                }

                try
                {
                    InvokeStatic(module, "SpawnItem", item, pos.Value, Quaternion.identity, true);
                    Log.LogInfo($"Item spawneado: {item.name} en {pos.Value}");
                }
                catch (Exception ex)
                {
                    Log.LogError($"Fallo SpawnItem de REPOLib: {ex.Message}");
                    DescribeModule(module);
                    return;
                }
            }
        }

        // Loguea los nombres disponibles para usar en spawn_enemy / spawn_item.
        private static void ListModuleNames(string typeName, string label, params string[] memberNames)
        {
            Type module = FindRepoLibType(typeName);
            if (module == null)
            {
                Log.LogError("No se encontro REPOLib. Instalar el mod Zehs-REPOLib (Thunderstore) en BepInEx\\plugins.");
                return;
            }

            var all = GetAllFromModule(module, memberNames);
            if (all.Count == 0)
            {
                Log.LogWarning($"REPOLib no devolvio la lista de {label}.");
                DescribeModule(module);
                return;
            }

            Log.LogInfo($"Nombres de {label} disponibles ({all.Count}): {string.Join(" | ", all.Select(o => o.name).ToArray())}");
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
