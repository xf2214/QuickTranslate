using System.Threading;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Translation;

namespace QuickTranslate.Infrastructure.Cache;

public class MemoryLruTranslationCache : ITranslationCache
{
    private readonly int _capacity;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, TranslationResult Value)>> _map;
    private readonly LinkedList<(string Key, TranslationResult Value)> _list;

    public int Capacity => _capacity;

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _map.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public MemoryLruTranslationCache(int capacity = 1000)
    {
        _capacity = capacity;
        _map = new Dictionary<string, LinkedListNode<(string, TranslationResult)>>(capacity + 1);
        _list = new LinkedList<(string, TranslationResult)>();
    }

    public bool TryGet(string normalizedKey, out TranslationResult value)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            if (_map.TryGetValue(normalizedKey, out var node))
            {
                _lock.EnterWriteLock();
                try
                {
                    _list.Remove(node);
                    _list.AddFirst(node);
                }
                finally
                {
                    _lock.ExitWriteLock();
                }

                value = node.Value.Value;
                return true;
            }

            value = default!;
            return false;
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
    }

    public void Add(string normalizedKey, TranslationResult value)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_map.TryGetValue(normalizedKey, out var existing))
            {
                _list.Remove(existing);
                _map.Remove(normalizedKey);
            }

            var node = new LinkedListNode<(string, TranslationResult)>((normalizedKey, value));
            _list.AddFirst(node);
            _map[normalizedKey] = node;

            while (_map.Count > _capacity && _list.Last != null)
            {
                var last = _list.Last;
                _map.Remove(last.Value.Key);
                _list.RemoveLast();
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Remove(string normalizedKey)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_map.TryGetValue(normalizedKey, out var node))
            {
                _list.Remove(node);
                _map.Remove(normalizedKey);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Clear()
    {
        _lock.EnterWriteLock();
        try
        {
            _map.Clear();
            _list.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
}
