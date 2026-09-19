namespace KREAN.Core.ECS;

public delegate void EntityAction<T1>(Entity entity, ref T1 a)
    where T1 : struct;

public delegate void EntityAction<T1, T2>(Entity entity, ref T1 a, ref T2 b)
    where T1 : struct where T2 : struct;

public delegate void EntityAction<T1, T2, T3>(Entity entity, ref T1 a, ref T2 b, ref T3 c)
    where T1 : struct where T2 : struct where T3 : struct;

/// <summary>
/// Holds all entities and components.
/// Queries iterate backwards so removing the *current* entity's component inside a query is safe.
/// Do not create/destroy OTHER entities inside a query.
/// </summary>
public sealed class World
{
    readonly List<int> _generations = new();
    readonly List<bool> _alive = new();
    readonly Stack<int> _freeIds = new();
    readonly Dictionary<Type, IComponentPool> _pools = new();

    public int EntityCount { get; private set; }

    // ---------- entities ----------

    public Entity CreateEntity()
    {
        int id;
        if (_freeIds.Count > 0)
        {
            id = _freeIds.Pop();
            _alive[id] = true;
        }
        else
        {
            id = _generations.Count;
            _generations.Add(0);
            _alive.Add(true);
        }
        EntityCount++;
        return new Entity(id, _generations[id]);
    }

    public bool IsAlive(Entity e) =>
        e.Id >= 0 && e.Id < _generations.Count && _alive[e.Id] && _generations[e.Id] == e.Generation;

    public void Destroy(Entity e)
    {
        if (!IsAlive(e)) return;
        foreach (var pool in _pools.Values) pool.Remove(e.Id);
        _alive[e.Id] = false;
        _generations[e.Id]++;
        _freeIds.Push(e.Id);
        EntityCount--;
    }

    public IEnumerable<Entity> AllEntities()
    {
        for (int id = 0; id < _generations.Count; id++)
            if (_alive[id]) yield return new Entity(id, _generations[id]);
    }

    // ---------- components ----------

    public ComponentPool<T> Pool<T>() where T : struct => (ComponentPool<T>)GetOrCreatePool(typeof(T));

    public ref T Add<T>(Entity e, in T component) where T : struct
    {
        if (!IsAlive(e)) throw new InvalidOperationException($"{e} is not alive");
        return ref Pool<T>().Add(e.Id, in component);
    }

    public ref T Get<T>(Entity e) where T : struct => ref Pool<T>().Get(e.Id);

    public bool Has<T>(Entity e) where T : struct => IsAlive(e) && Pool<T>().Has(e.Id);

    public void Remove<T>(Entity e) where T : struct => Pool<T>().Remove(e.Id);

    // ---------- queries ----------

    public void Query<T1>(EntityAction<T1> action) where T1 : struct
    {
        var p1 = Pool<T1>();
        for (int i = p1.Count - 1; i >= 0; i--)
        {
            int id = p1.EntityAt(i);
            action(new Entity(id, _generations[id]), ref p1.DataAt(i));
        }
    }

    public void Query<T1, T2>(EntityAction<T1, T2> action) where T1 : struct where T2 : struct
    {
        var p1 = Pool<T1>();
        var p2 = Pool<T2>();

        if (p1.Count <= p2.Count)
        {
            for (int i = p1.Count - 1; i >= 0; i--)
            {
                int id = p1.EntityAt(i);
                if (!p2.Has(id)) continue;
                action(new Entity(id, _generations[id]), ref p1.DataAt(i), ref p2.Get(id));
            }
        }
        else
        {
            for (int i = p2.Count - 1; i >= 0; i--)
            {
                int id = p2.EntityAt(i);
                if (!p1.Has(id)) continue;
                action(new Entity(id, _generations[id]), ref p1.Get(id), ref p2.DataAt(i));
            }
        }
    }

    /// <summary>Put the rarest component first – it drives the iteration.</summary>
    public void Query<T1, T2, T3>(EntityAction<T1, T2, T3> action)
        where T1 : struct where T2 : struct where T3 : struct
    {
        var p1 = Pool<T1>();
        var p2 = Pool<T2>();
        var p3 = Pool<T3>();

        for (int i = p1.Count - 1; i >= 0; i--)
        {
            int id = p1.EntityAt(i);
            if (!p2.Has(id) || !p3.Has(id)) continue;
            action(new Entity(id, _generations[id]), ref p1.DataAt(i), ref p2.Get(id), ref p3.Get(id));
        }
    }

    // ---------- boxed access (serialization / editor inspector) ----------

    public IEnumerable<(Type Type, object Value)> GetAllComponents(Entity e)
    {
        foreach (var pool in _pools.Values)
            if (pool.Has(e.Id))
                yield return (pool.ComponentType, pool.GetBoxed(e.Id));
    }

    public void SetComponent(Entity e, Type type, object value) =>
        GetOrCreatePool(type).SetBoxed(e.Id, value);

    IComponentPool GetOrCreatePool(Type type)
    {
        if (!_pools.TryGetValue(type, out var pool))
        {
            var poolType = typeof(ComponentPool<>).MakeGenericType(type);
            pool = (IComponentPool)Activator.CreateInstance(poolType)!;
            _pools[type] = pool;
        }
        return pool;
    }
}
