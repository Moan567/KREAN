namespace KREAN.Core.ECS;

public interface IComponentPool
{
    Type ComponentType { get; }
    int Count { get; }
    bool Has(int entityId);
    void Remove(int entityId);
    object GetBoxed(int entityId);
    void SetBoxed(int entityId, object value);
}

/// <summary>Sparse-set storage: O(1) add/remove/lookup, tightly packed data for fast iteration.</summary>
public sealed class ComponentPool<T> : IComponentPool where T : struct
{
    int[] _sparse = Array.Empty<int>();   // entityId -> dense index (-1 = none)
    int[] _entities = new int[16];        // dense index -> entityId
    T[] _data = new T[16];                // dense index -> component
    int _count;

    public Type ComponentType => typeof(T);
    public int Count => _count;

    public bool Has(int entityId) => (uint)entityId < (uint)_sparse.Length && _sparse[entityId] >= 0;
    public int EntityAt(int index) => _entities[index];
    public ref T DataAt(int index) => ref _data[index];

    public ref T Get(int entityId)
    {
        if (!Has(entityId))
            throw new KeyNotFoundException($"Entity {entityId} has no component {typeof(T).Name}");
        return ref _data[_sparse[entityId]];
    }

    public ref T Add(int entityId, in T value)
    {
        EnsureSparse(entityId);

        int existing = _sparse[entityId];
        if (existing >= 0)
        {
            _data[existing] = value;
            return ref _data[existing];
        }

        if (_count == _data.Length)
        {
            Array.Resize(ref _data, _count * 2);
            Array.Resize(ref _entities, _count * 2);
        }

        _sparse[entityId] = _count;
        _entities[_count] = entityId;
        _data[_count] = value;
        _count++;
        return ref _data[_count - 1];
    }

    public void Remove(int entityId)
    {
        if (!Has(entityId)) return;

        int index = _sparse[entityId];
        int last = _count - 1;

        if (index != last)
        {
            _data[index] = _data[last];
            _entities[index] = _entities[last];
            _sparse[_entities[index]] = index;
        }

        _sparse[entityId] = -1;
        _data[last] = default;
        _count--;
    }

    public object GetBoxed(int entityId) => Get(entityId);
    public void SetBoxed(int entityId, object value) => Add(entityId, (T)value);

    void EnsureSparse(int entityId)
    {
        if (entityId < _sparse.Length) return;
        int old = _sparse.Length;
        int size = Math.Max(entityId + 1, Math.Max(64, old * 2));
        Array.Resize(ref _sparse, size);
        Array.Fill(_sparse, -1, old, size - old);
    }
}
