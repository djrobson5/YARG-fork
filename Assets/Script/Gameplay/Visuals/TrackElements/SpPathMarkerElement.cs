using System.Collections.Generic;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Gameplay.Player;
using YARG.Gameplay.SpPath;

namespace YARG.Gameplay.Visuals
{
    /// <summary>
    /// The highway half of the Star Power path cue: a short green band with bright rail caps at
    /// the activation note, a ring around the note(s) to hit, and a tick on the beat before it
    /// (<c>docs/sp-path-design.md</c> → "Visual redesign, 2026-09-04").
    /// </summary>
    /// <remarks>
    /// Everything is built at runtime from clones of the beatline prefab's quad, so the marker
    /// inherits the beatline material — and with it the highway curve and fade shaders — without
    /// a single prefab, material or scene edit. The colour is the drum Star Power *activation*
    /// green upstream already uses (<c>Assets/Art/Materials/Gameplay/Track/Effects/DrumSPActivationTrim.mat</c>),
    /// deliberately not Star Power orange: the whole point of the redesign is that everything
    /// near the marker was already orange.
    /// </remarks>
    public class SpPathMarkerElement : TrackElement<TrackPlayer>
    {
        /// The activation green's trim colour, <c>#52FF00</c> — <c>DrumSPActivationTrim.mat</c>'s
        /// <c>_Color</c>. The default the <c>StarPowerPathColor</c> setting ships with.
        public static readonly Color DefaultCueColor = new(0.32132697f, 1f, 0f, 1f);

        /// The bright half of the cue: rails, ring, lead-in tick, the recoloured activation note,
        /// the strike line glow and the HUD chip. Driven by <c>Settings.StarPowerPathColor</c>
        /// through <see cref="SetCueColor"/>, which the track player calls once per song.
        public static Color ActivationTrimColor { get; private set; } = DefaultCueColor;

        /// The dark body the band is tinted with so it reads as a wash rather than a solid slab.
        /// Derived from the cue colour rather than configured: the same hue and saturation at
        /// about a third of the value, the way <c>#005400</c> relates to <c>#52FF00</c>.
        public static Color ActivationBodyColor { get; private set; }
            = DeriveBodyColor(DefaultCueColor);

        /// How much of the cue colour's value the band body keeps.
        private const float BODY_VALUE_SCALE = 0.33f;

        /// <summary>
        /// Points the whole cue at <paramref name="color"/>, deriving the band body tint from it.
        /// </summary>
        /// <remarks>
        /// Static because every piece of the cue — geometry built here, the note recolour in
        /// <c>FiveFretGuitarNoteElement</c>, and the HUD chip — reads the same pair, and the
        /// setting behind it is global rather than per player. Called from
        /// <c>TrackPlayer.OnStarPowerPathSet</c>, before any marker or note is spawned for the
        /// path, so changing the setting mid-song does nothing until the next run.
        /// </remarks>
        public static void SetCueColor(Color color)
        {
            color.a = 1f;

            ActivationTrimColor = color;
            ActivationBodyColor = DeriveBodyColor(color);
        }

        private static Color DeriveBodyColor(Color color)
        {
            Color.RGBToHSV(color, out float h, out float s, out float v);
            var body = Color.HSVToRGB(h, s, v * BODY_VALUE_SCALE);
            body.a = 1f;
            return body;
        }

        /// The most lanes any marker ever rings. Five-fret is the only instrument with a path.
        private const int MAX_RING_LANES = 5;

        // Heights above the highway. The beatline quad itself sits at y = 0.002, so everything
        // here is above it and the pieces are stacked in the order they should occlude in.
        private const float BAND_Y = 0.0030f;
        private const float RAIL_Y = 0.0034f;
        private const float LEAD_Y = 0.0034f;
        private const float RING_Y = 0.0040f;

        private const float BAND_ALPHA      = 0.17f;
        private const float RAIL_ALPHA      = 0.85f;
        private const float LEAD_ALPHA      = 0.50f;
        private const float RING_ALPHA      = 1.00f;

        /// Width of a rail cap across the highway, in track units.
        private const float RAIL_WIDTH = 0.09f;

        /// Thickness of a ring edge, and how long the ring is along the highway.
        private const float RING_THICKNESS = 0.035f;
        private const float RING_LENGTH    = 0.30f;

        /// How much of a lane the ring spans.
        private const float RING_LANE_FRACTION = 0.86f;

        private const float LEAD_TICK_WIDTH  = 1.30f;
        private const float LEAD_TICK_LENGTH = 0.06f;

        private Transform _geometryParent;

        private MeshRenderer _band;
        private MeshRenderer _railLeft;
        private MeshRenderer _railRight;
        private MeshRenderer _leadTick;

        /// Four edges per lane, in the order top, bottom, left, right.
        private readonly MeshRenderer[] _ringEdges = new MeshRenderer[MAX_RING_LANES * 4];

        /// Whether the colours have been pushed into the materials for this spawn.
        private bool _hasAppliedState;

        /// <summary>The activation this marker sits on. Set by the spawner before enabling.</summary>
        public Activation ActivationRef;

        /// <summary>
        /// Chart time of the beat the lead-in tick sits on, and how long the band is in seconds
        /// (one beat). Both are computed from the chart's beatlines by the spawner, because the
        /// element has no business knowing the sync track.
        /// </summary>
        public double LeadInTime;
        public double BandDuration = 0.5;

        /// <summary>
        /// Highway X positions of the note(s) to ring, in track units. Empty for an open or
        /// full-width note, where the band already spans everything there is to ring.
        /// </summary>
        public IReadOnlyList<float> RingLaneXPositions;

        public override double ElementTime => ActivationRef.ActivationTime;

        // The band is centred on the activation, so half of it is still behind the strike line
        // when the activation instant arrives. Keep the element alive long enough for that half
        // to scroll out rather than popping the whole marker at the strike line. The half-band is
        // a beat long in track units, which on a slow chart at a high note speed is several units
        // on its own, so it has to be part of the offset rather than a constant guess.
        protected override float RemovePointOffset => 2f + _bandHalfLength;

        /// Half the band's length along the highway, in track units. Set with the geometry.
        private float _bandHalfLength;

        protected override void GameplayAwake()
        {
            BuildGeometry();

            base.GameplayAwake();
        }

        /// <summary>
        /// Clones the beatline quad into every piece this marker draws, once per pooled object.
        /// </summary>
        private void BuildGeometry()
        {
            var template = GetComponentInChildren<MeshRenderer>(true);
            if (template == null)
            {
                YargLogger.LogWarning("SP path: the marker template has no MeshRenderer; " +
                    "nothing will draw.");
                return;
            }

            _geometryParent = template.transform.parent != null
                ? template.transform.parent
                : transform;

            // The template quad itself becomes the band, so one clone is saved and the marker
            // keeps working even if the prefab layout changes underneath it.
            _band = template;

            _railLeft = CloneQuad(template, "Rail Left");
            _railRight = CloneQuad(template, "Rail Right");
            _leadTick = CloneQuad(template, "Lead-in Tick");

            for (int lane = 0; lane < MAX_RING_LANES; lane++)
            {
                for (int edge = 0; edge < 4; edge++)
                {
                    _ringEdges[lane * 4 + edge] = CloneQuad(template, $"Ring {lane}.{edge}");
                }
            }
        }

        private MeshRenderer CloneQuad(MeshRenderer template, string name)
        {
            var clone = Instantiate(template.gameObject, _geometryParent);
            clone.name = name;
            clone.transform.localRotation = template.transform.localRotation;
            return clone.GetComponent<MeshRenderer>();
        }

        protected override void InitializeElement()
        {
            transform.localPosition = Vector3.zero;

            if (_band == null)
            {
                return;
            }

            float speed = Player.NoteSpeed;
            float bandLength = Mathf.Max(0.15f, (float) BandDuration * speed);
            _bandHalfLength = bandLength / 2f;

            // Centred on the activation instant: half the band leads the note in, half trails it,
            // which is what makes the note itself sit in the middle of a lit stripe.
            SetQuad(_band, 0f, BAND_Y, 0f, TrackPlayer.TRACK_WIDTH, bandLength);

            float railX = (TrackPlayer.TRACK_WIDTH - RAIL_WIDTH) / 2f;
            SetQuad(_railLeft, -railX, RAIL_Y, 0f, RAIL_WIDTH, bandLength);
            SetQuad(_railRight, railX, RAIL_Y, 0f, RAIL_WIDTH, bandLength);

            // Negative Z is earlier in time: the lead-in beat reaches the strike line first.
            float leadZ = -Mathf.Max(0f, (float) (ActivationRef.ActivationTime - LeadInTime)) * speed;
            SetQuad(_leadTick, 0f, LEAD_Y, leadZ, LEAD_TICK_WIDTH, LEAD_TICK_LENGTH);

            int laneCount = Mathf.Max(1, Player.LaneCount);
            float ringWidth = TrackPlayer.TRACK_WIDTH / laneCount * RING_LANE_FRACTION;
            int rings = RingLaneXPositions?.Count ?? 0;

            for (int lane = 0; lane < MAX_RING_LANES; lane++)
            {
                bool used = lane < rings;
                for (int edge = 0; edge < 4; edge++)
                {
                    var renderer = _ringEdges[lane * 4 + edge];
                    if (renderer == null)
                    {
                        continue;
                    }

                    renderer.gameObject.SetActive(used);
                }

                if (!used)
                {
                    continue;
                }

                float x = RingLaneXPositions[lane];
                float halfW = ringWidth / 2f;
                float halfL = RING_LENGTH / 2f;

                SetQuad(_ringEdges[lane * 4 + 0], x, RING_Y, halfL, ringWidth, RING_THICKNESS);
                SetQuad(_ringEdges[lane * 4 + 1], x, RING_Y, -halfL, ringWidth, RING_THICKNESS);
                SetQuad(_ringEdges[lane * 4 + 2], x - halfW, RING_Y, 0f, RING_THICKNESS, RING_LENGTH);
                SetQuad(_ringEdges[lane * 4 + 3], x + halfW, RING_Y, 0f, RING_THICKNESS, RING_LENGTH);
            }

            _hasAppliedState = false;
            ApplyColors();
        }

        private static void SetQuad(MeshRenderer renderer, float x, float y, float z,
            float width, float length)
        {
            if (renderer == null)
            {
                return;
            }

            var quad = renderer.transform;

            // The quad is rotated +90 about X, so its local Y axis lies along the highway and its
            // local X across it — exactly the convention Beatline.prefab is authored in.
            quad.localScale = new Vector3(width, length, 1f);
            quad.localPosition = new Vector3(x, y, z);
        }

        protected override void UpdateElement()
        {
        }

        protected override void HideElement()
        {
        }

        /// <summary>
        /// Paints the marker in the one and only state it has.
        /// </summary>
        /// <remarks>
        /// There used to be a second, dimmed state driven by <c>TrackPlayer.SpPathDiverged</c>.
        /// It was removed on the user's instruction (2026-09-04): the path is information about
        /// where the *next* run should activate, so it is worth exactly as much after a dropped
        /// phrase as before one, and a cue that fades out is a cue that cannot be read. The
        /// divergence detection itself is still there, purely as a diagnostic log.
        /// </remarks>
        private void ApplyColors()
        {
            if (_hasAppliedState)
            {
                return;
            }

            _hasAppliedState = true;

            SetActiveAndColor(_band, true, ActivationBodyColor, BAND_ALPHA);
            SetActiveAndColor(_railLeft, true, ActivationTrimColor, RAIL_ALPHA);
            SetActiveAndColor(_railRight, true, ActivationTrimColor, RAIL_ALPHA);
            SetActiveAndColor(_leadTick, true, ActivationTrimColor, LEAD_ALPHA);

            foreach (var edge in _ringEdges)
            {
                if (edge == null || !edge.gameObject.activeSelf)
                {
                    continue;
                }

                SetColor(edge, ActivationTrimColor, RING_ALPHA);
            }
        }

        private static void SetActiveAndColor(MeshRenderer renderer, bool active, Color color,
            float alpha)
        {
            if (renderer == null)
            {
                return;
            }

            renderer.gameObject.SetActive(active);
            if (active)
            {
                SetColor(renderer, color, alpha);
            }
        }

        private static void SetColor(MeshRenderer renderer, Color color, float alpha)
        {
            var material = renderer.material;
            color.a = alpha;
            material.color = color;
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
        public static Pool CreateRuntimePool(Pool beatlinePool, int prewarmAmount = 3,
            int objectCap = 12)
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

        /// <summary>
        /// Clones the beatline quad into a standalone, always-on object — the steady green glow
        /// laid over the strike line while an activation is due.
        /// </summary>
        /// <remarks>
        /// Same trick as the pool: the beatline prefab is the only quad on the highway this code
        /// can reach without touching an asset, and it already carries a material the curve and
        /// fade shaders understand.
        /// </remarks>
        public static MeshRenderer CreateStrikeLineGlow(Pool beatlinePool, float length,
            float alpha)
        {
            if (beatlinePool == null || beatlinePool.Prefab == null)
            {
                return null;
            }

            var quad = beatlinePool.Prefab.GetComponentInChildren<MeshRenderer>(true);
            if (quad == null)
            {
                YargLogger.LogWarning("Cannot create the Star Power path strike line glow: " +
                    "the beatline prefab has no MeshRenderer.");
                return null;
            }

            var root = new GameObject("SP Path Strike Line Glow");
            var rootTransform = root.transform;
            var beatlineTransform = beatlinePool.transform;
            rootTransform.SetParent(beatlineTransform.parent, false);
            rootTransform.localPosition = beatlineTransform.localPosition;
            rootTransform.localRotation = beatlineTransform.localRotation;
            rootTransform.localScale = beatlineTransform.localScale;

            var clone = Instantiate(quad.gameObject, rootTransform);
            clone.name = "Glow";
            var renderer = clone.GetComponent<MeshRenderer>();

            var cloneTransform = clone.transform;
            cloneTransform.localRotation = quad.transform.localRotation;
            SetQuad(renderer, 0f, RAIL_Y, TrackPlayer.STRIKE_LINE_POS,
                TrackPlayer.TRACK_WIDTH, length);
            SetColor(renderer, ActivationTrimColor, alpha);

            // The root stays live so the caller can toggle the glow itself with SetActive.
            clone.SetActive(false);
            return renderer;
        }
    }
}
