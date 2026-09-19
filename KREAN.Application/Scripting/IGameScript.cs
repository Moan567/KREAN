using KREAN.Core.ECS;

namespace KREAN.Application.Scripting;

/// <summary>
/// Marker for scripts compiled from KREAN.Application/Scripts.
/// Any class implementing this (or plain ISystem) will be discovered and added to the Engine.
/// Scripts are stored as .cs files under KREAN.Application/Scripts and compiled with Roslyn at startup / on hot reload.
/// </summary>
public interface IGameScript : ISystem
{
}
