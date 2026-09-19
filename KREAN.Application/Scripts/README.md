# KREAN Scripts

Scripts are **stored and compiled from `KREAN.Application/Scripts`** via Roslyn.

- Add any `.cs` file under this folder (subfolders allowed).
- It will be compiled at startup by `ScriptCompiler` (`Scripting/ScriptCompiler.cs`) with references to `KREAN.Core`, `KREAN.Runtime`, `KREAN.MapCompiler` and framework libs.
- Any `class` implementing `ISystem` or `IGameScript` (`Scripting/IGameScript.cs`) is auto-discovered and added to the `Engine` (`Game/GameApp.cs`).
- Constructor injection is supported: `World`, `CollisionWorld`, `InputState` are injected if your ctor asks for them.
- **Hot reload**: `ScriptManager` watches the folder. Save a file and it recompiles (~400ms debounce) and adds new systems. Restart the app for a clean reload (old systems remain until restart).

## Examples

- `Examples/SpinSystem.cs` — spins entities whose `EntityName` contains "spin".
- `Examples/PlayerTweak.cs` — shows how to tweak character movement (sprint, etc.) from a script instead of hardcoding in `KREAN.Runtime/Systems/PlayerMoveSystem.cs`.
- `Examples/TriggerFeedback.cs` — reacts to `TriggerVolume` components generated from `trigger_*` brushes in the `.map`.

## Adding your own

```csharp
using KREAN.Application.Scripting;
using KREAN.Core.ECS;
using KREAN.Core.Components;

public sealed class MySystem : IGameScript
{
    public void Update(World world, float dt)
    {
        world.Query<Transform>((Entity e, ref Transform t) => {
            t.Position.Y += dt;
        });
    }
}
```

Set the entity to trigger it via a key in TrenchBroom: add a custom key `script` or rely on component queries as above. All keys from the `.map` are available in `EntityProperties.Values`.

## Running

```
dotnet run --project KREAN.Application -- level.map
dotnet run --project KREAN.Application -- level.scene.json
dotnet run --project KREAN.Application -- --scripts path/to/Scripts
dotnet run --project KREAN.Application -- --help
```

If no scene exists, the `.map` is compiled to a temp `.scene.json` and queued. The Editor's **Play** button saves the `.map` and launches `KREAN.Application` with it.
