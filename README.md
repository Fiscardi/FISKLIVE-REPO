# FiskLiveREPO

Mod de [R.E.P.O.](https://store.steampowered.com/app/3241660/REPO/) para FiskLive. Corre dentro del juego (via BepInEx) y escucha comandos por TCP en el puerto `8422`, mismo patrón que `FiskLiveGTA.cs` para GTA V.

## Estado

Este es un primer armado. Las acciones están separadas en dos grupos (marcados en el código con comentarios `[CONFIABLE]` / `[NECESITA AJUSTE]`):

- **Confiables** (gravedad, cámara lenta/rápida, apagar luces): usan solo `UnityEngine` puro, no dependen de nada específico de R.E.P.O., deberían andar tal cual.
- **Necesitan ajuste** (curar/dañar jugador, dios, teletransportar, spawnear enemigos/objetos): tocan clases internas del juego (`PlayerAvatar`, `PlayerHealth`, `EnemyDirector`). Están escritas con reflection (buscan la clase/método por texto) para que, si algún nombre está mal, falle en el log del juego en vez de romper la compilación de todo el mod. **Es esperable tener que ajustar estas con el juego abierto y el log real delante.**

## Instalación (para probar)

1. Instalar [BepInEx 5.4.23.5](https://thunderstore.io/c/repo/p/BepInEx/BepInExPack/) en la carpeta de R.E.P.O. (donde está el .exe del juego).
2. Abrir el juego una vez para que BepInEx genere sus carpetas.
3. Copiar `FiskLiveREPO.dll` (el artifact que genera este repo en Actions) a `<carpeta del juego>/BepInEx/plugins/`.
4. Abrir el juego. En la consola de BepInEx debería aparecer `FiskLiveREPO cargado, escuchando comandos en el puerto 8422`.

## Ver los errores de las acciones "necesita ajuste"

Con BepInEx instalado, la consola de logs se abre sola al iniciar el juego (o revisar `BepInEx/LogOutput.log`). Ahí van a aparecer los `LogError`/`LogWarning` de cada acción que todavía no esté confirmada, indicando exactamente qué clase o método no se encontró — esa información es la que hace falta pasar para ajustar el código.

## Comandos soportados

| action | parámetros | estado |
|---|---|---|
| `time_scale` | `scale` (float), `seconds` (int) | ✅ confiable |
| `freeze_world_physics` | `seconds` (int) | ✅ confiable |
| `gravity_set` | `preset` (`invertida`/`liviana`/`pesada`), `seconds` (int) | ✅ confiable |
| `blackout` | `seconds` (int) | ✅ confiable |
| `restore_lighting` | — | ✅ confiable |
| `heal_player` | `amount` (int) | ⚠️ necesita ajuste |
| `damage_player` | `amount` (int) | ⚠️ necesita ajuste |
| `god_mode_toggle` | — | ⚠️ necesita ajuste |
| `teleport_random` | — | ⚠️ necesita ajuste |
| `spawn_enemy` | `enemy` (string, opcional) | ⚠️ necesita ajuste |
| `enemy_horde` | `count` (int) | ⚠️ necesita ajuste |
| `freeze_enemies` / `unfreeze_enemies` | — | ⚠️ necesita ajuste |
| `spawn_valuable` | `name` (string) | ⚠️ necesita ajuste |
| `spawn_item` | `name` (string) | ⚠️ necesita ajuste |

## Créditos de referencia técnica

- [BepInEx](https://github.com/BepInEx/BepInEx) — framework de modding.
- [R.E.P.O.GameLibs.Steam](https://www.nuget.org/packages/R.E.P.O.GameLibs.Steam) — librerías públicas (solo firmas, sin código del juego) generadas por [GameLib Dehumidifier](https://github.com/Lordfirespeed/NuGet-GameLib-Dehumidifier).
- [REPOLib](https://github.com/ZehsTeam/REPOLib) — librería de la comunidad para registrar/spawnear contenido de forma segura en multiplayer.
