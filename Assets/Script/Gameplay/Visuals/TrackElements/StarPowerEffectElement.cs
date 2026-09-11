using UnityEngine;

namespace YARG.Gameplay.Visuals
{
    public class StarPowerEffectElement : MonoBehaviour
    {
        private const float ANIM_LENGTH = 1f;

        // Safe amount backwards from the origin that we can assume the start position is
        private const float START_POSITION = 3f;

        private readonly int _animTimestampId = Shader.PropertyToID("_AnimTimestamp");

        // Don't immediately play the effect at the start if animations are disabled
        private float _animTimestamp = float.NegativeInfinity;

        public void Initialize()
        {
            // Assuming that the Z length of the effect is exactly one unit, scale the effect
            float endPosition = GameObject.Find("HUD Location").transform.position.z;
            float totalSpan = endPosition + START_POSITION;
            transform.localScale = transform.localScale.WithZ(transform.localScale.z * totalSpan);

            // Assuming that the effect is spawned at the origin, move the effect
            float zOffset = totalSpan / 2 - START_POSITION;
            transform.localScale = transform.localScale.AddZ(zOffset);
        }

        public void PlayAnimation()
        {
            _animTimestamp = 0f;
        }

        /// <summary>
        /// Takes the trim back to its unplayed state, without waiting out the animation.
        /// </summary>
        /// <remarks>
        /// The timestamp is a latch: <see cref="Update"/> only ever advances it, and only ever
        /// hides the object once it has run past the animation length. A seek that lands while
        /// the trim is lit would otherwise leave it lit until the animation happens to finish,
        /// and the object inactive here means <see cref="Update"/> is not running to finish it.
        /// </remarks>
        public void ForceReset()
        {
            // Past the end rather than back at the start, so that Update agrees: were the object
            // re-enabled without a PlayAnimation, its first frame would hide it again.
            _animTimestamp = ANIM_LENGTH + 1f;
            gameObject.SetActive(false);
        }

        private void Update()
        {
            if (_animTimestamp > ANIM_LENGTH)
            {
                gameObject.SetActive(false);
                return;
            }

            _animTimestamp += Time.deltaTime;

            foreach (var meshRenderer in GetComponentsInChildren<MeshRenderer>())
            {
                foreach (var material in meshRenderer.materials)
                {
                    material.SetFloat(_animTimestampId, _animTimestamp);
                }
            }
        }
    }
}
