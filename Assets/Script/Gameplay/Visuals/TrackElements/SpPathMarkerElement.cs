using UnityEngine;
using YARG.Core.Logging;
using YARG.Gameplay.Player;
using YARG.Gameplay.SpPath;

namespace YARG.Gameplay.Visuals
{
    /// <summary>
    /// A single band across the highway at an optimal Star Power activation note
    /// (<c>docs/sp-path-design.md</c> §5.1).
    /// </summary>
    /// <remarks>
    /// Geometry and pooling are <see cref="BeatlineElement"/>'s, deliberately: the locked design
    /// is "a measure line, in Star Power orange". Rather than duplicate
    /// <c>Beatline.prefab</c> — a hand-written prefab is unverifiable without opening the editor —
    /// <see cref="CreateRuntimePool"/> clones the beatline prefab at runtime and swaps the element
    /// component, so nothing on disk has to change.
    /// </remarks>
    public class SpPathMarkerElement : TrackElement<TrackPlayer>
    {
        /// Matches <c>BeatlineElement.MEASURE_SCALE</c> — the marker is as thick as a measure line.
        private const float MARKER_SCALE = 0.07f;

        /// Just above the beatline quad's own <c>y = 0.002</c>, so the two never z-fight.
        private const float MARKER_Y = 0.003f;

        private const float FULL_ALPHA   = 1f;
        private const float DIMMED_ALPHA = 0.25f;

        private MeshRenderer _meshRenderer;
        private Material     _material;

        /// The last alpha pushed into the material, so the diverged check is free per frame.
        private float _appliedAlpha = float.NaN;

        /// <summary>The activation this marker sits on. Set by the spawner before enabling.</summary>
        public Activation ActivationRef;

        /// <summary>
        /// The player's Star Power colour, read from their highway preset by the spawner so this
        /// element never hardcodes it.
        /// </summary>
        public Color MarkerColor = Color.white;

        public override double ElementTime => ActivationRef.ActivationTime;

        protected override void GameplayAwake()
        {
            // Found rather than serialized: this component is added to a prefab clone at runtime,
            // so there is no inspector pass to wire a reference in.
            _meshRenderer = GetComponentInChildren<MeshRenderer>(true);

            base.GameplayAwake();
        }

        protected override void InitializeElement()
        {
            transform.localPosition = Vector3.zero;

            var cachedTransform = _meshRenderer.transform;
            cachedTransform.localScale = cachedTransform.localScale.WithY(MARKER_SCALE);

            // The mesh is a clone of the beatline quad, which sits at y = 0.002. A marker landing
            // on a beat line would be coplanar with it and z-fight; lift it a hair clear.
            cachedTransform.localPosition = cachedTransform.localPosition.WithY(MARKER_Y);

            _material = _meshRenderer.material;
            _appliedAlpha = float.NaN;

            ApplyColor();
        }

        protected override void UpdateElement()
        {
            // The dim is a per-player flag that can flip at any moment, and it applies to markers
            // already on the highway as well as the ones still to come.
            ApplyColor();
        }

        protected override void HideElement()
        {
        }

        private void ApplyColor()
        {
            float alpha = Player.SpPathDiverged ? DIMMED_ALPHA : FULL_ALPHA;
            if (alpha.Equals(_appliedAlpha))
            {
                return;
            }

            _appliedAlpha = alpha;

            var color = MarkerColor;
            color.a = alpha;
            _material.color = color;
        }

        /// <summary>
        /// Builds a marker pool from the player's existing beatline pool, with no prefab or scene
        /// edits: the beatline prefab is cloned, its <see cref="BeatlineElement"/> swapped for a
        /// <see cref="SpPathMarkerElement"/>, and the clone used as the new pool's prefab.
        /// </summary>
        /// <remarks>
        /// The pool object is parented next to the beatline pool and given its local transform, so
        /// markers land in exactly the same space beatlines do.
        /// </remarks>
        public static Pool CreateRuntimePool(Pool beatlinePool, int prewarmAmount = 4,
            int objectCap = 16)
        {
            if (beatlinePool == null || beatlinePool.Prefab == null)
            {
                YargLogger.LogWarning("Cannot create the Star Power path marker pool: " +
                    "the beatline pool it copies is missing.");
                return null;
            }

            var beatlineTransform = beatlinePool.transform;

            var poolObject = new GameObject("SP Path Marker Pool");
            var poolTransform = poolObject.transform;
            poolTransform.SetParent(beatlineTransform.parent, false);
            poolTransform.localPosition = beatlineTransform.localPosition;
            poolTransform.localRotation = beatlineTransform.localRotation;
            poolTransform.localScale = beatlineTransform.localScale;

            // Inactive while it is being built, so neither the pool's prewarm nor the half-swapped
            // template's Awake runs before everything is in place.
            poolObject.SetActive(false);

            var pool = poolObject.AddComponent<Pool>();

            var template = Instantiate(beatlinePool.Prefab, poolTransform);
            template.name = "SP Path Marker";

            var beatlineElement = template.GetComponent<BeatlineElement>();
            if (beatlineElement == null)
            {
                YargLogger.LogWarning("The beatline prefab has no BeatlineElement on its root; " +
                    "cannot build the Star Power path marker pool.");
                Destroy(poolObject);
                return null;
            }

            // Immediate, because Pool looks the poolable up with GetComponent and a deferred
            // Destroy would leave two of them on the template for the rest of the frame.
            DestroyImmediate(beatlineElement);
            template.AddComponent<SpPathMarkerElement>();

            // Stays inactive once the pool object goes live; it is only ever cloned.
            template.SetActive(false);

            pool.ConfigureRuntime(template, prewarmAmount, objectCap);
            poolObject.SetActive(true);

            return pool;
        }
    }
}
