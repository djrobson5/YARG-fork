using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Serialization;
using YARG.Helpers.Extensions;

namespace YARG.Gameplay
{
    public interface IPoolable
    {
        public Pool ParentPool { get; set; }

        public void EnableFromPool();
        public void DisableIntoPool();
    }

    public class Pool : MonoBehaviour
    {
        // TODO: Reserialize everything and remove this
        [field: FormerlySerializedAs("_prefab")]
        [field: SerializeField]
        public GameObject Prefab { get; private set; }

        [SerializeField]
        private int _prewarmAmount = 300;
        [SerializeField]
        private int _objectCap = 500;

        private readonly Stack<IPoolable> _pooled = new();
        private readonly List<IPoolable> _spawnedObjects = new();

        private int TotalCount => _pooled.Count + _spawnedObjects.Count;

        public IReadOnlyList<IPoolable> AllSpawned => _spawnedObjects;

        protected virtual void Awake()
        {
            PrewarmTo(_prewarmAmount);
        }

        public void PrewarmTo(int targetTotalCount)
        {
            while (TotalCount < targetTotalCount)
            {
                var poolable = CreateNew();
                if (poolable == null)
                {
                    break;
                }

                _pooled.Push(poolable);
            }
        }

        /// <summary>
        /// Sets up a pool that was added from code rather than serialized in a prefab.
        /// </summary>
        /// <remarks>
        /// Must be called while the pool's GameObject is still inactive, i.e. before its
        /// <see cref="Awake"/> prewarm runs — there is no prefab to prewarm from until it is.
        /// </remarks>
        public void ConfigureRuntime(GameObject prefab, int prewarmAmount, int objectCap)
        {
            // Changing the prefab or the cap under live objects would leave the pool handing out
            // clones of the old prefab, and the cap arithmetic counting them.
            // Use SetPrefabAndReset for that.
            if (_pooled.Count > 0 || _spawnedObjects.Count > 0)
            {
                throw new InvalidOperationException(
                    "ConfigureRuntime must be called before the pool creates any objects.");
            }

            Prefab = prefab;
            _prewarmAmount = prewarmAmount;
            _objectCap = objectCap;
        }

        public void SetPrefabAndReset(GameObject newPrefab)
        {
            Prefab = newPrefab;

            transform.DestroyChildren();
            _pooled.Clear();
            _spawnedObjects.Clear();

            PrewarmTo(_prewarmAmount);
        }

        private IPoolable CreateNew()
        {
            if (TotalCount + 1 > _objectCap)
            {
                return null;
            }

            var gameObject = Instantiate(Prefab, transform);
            gameObject.SetActive(false);

            var poolable = gameObject.GetComponent<IPoolable>();
            poolable.ParentPool = this;

            return poolable;
        }

        public bool CanSpawnAmount(int count)
        {
            if (TotalCount + count <= _objectCap)
            {
                return true;
            }

            if (_pooled.Count >= count)
            {
                return true;
            }

            return false;
        }

        public IPoolable TakeWithoutEnabling()
        {
            if (_pooled.TryPop(out var poolable))
            {
                _spawnedObjects.Add(poolable);
                return poolable;
            }

            poolable = CreateNew();
            if (poolable != null)
            {
                _spawnedObjects.Add(poolable);
            }

            return poolable;
        }

        public IPoolable Take()
        {
            var poolable = TakeWithoutEnabling();
            if (poolable == null)
            {
                return null;
            }

            poolable.EnableFromPool();
            return poolable;
        }

        public void Return(IPoolable poolable)
        {
            // Skip if the stack already contains this poolable
            if (_pooled.Contains(poolable)) return;

            _spawnedObjects.Remove(poolable);

            poolable.DisableIntoPool();
            _pooled.Push(poolable);

            OnReturned(poolable);
        }

        public void ReturnAllObjects()
        {
            foreach (var poolable in _spawnedObjects.ToList())
            {
                Return(poolable);
            }
        }

        protected virtual void OnReturned(IPoolable poolable)
        {

        }
    }
}
