using UnityEngine;

namespace CloneStamp
{
    /// <summary>
    /// Drives the sample offset of the "AR/Clone Stamp Occlusion" material so the
    /// object is painted with camera pixels taken from beside it. In Auto mode the
    /// offset tracks the object's own screen-space width every frame, so the patch
    /// keeps coming from clean background next to the object as the phone moves.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(Renderer))]
    public class CloneStampOccluder : MonoBehaviour
    {
        public enum OffsetMode
        {
            /// <summary>Use <see cref="manualOffsetPixels"/> verbatim.</summary>
            Manual,

            /// <summary>Derive the offset from the object's screen-space bounds.</summary>
            AutoFromBounds
        }

        public enum SamplingSide
        {
            Right,
            Left,
            AutoPickSide
        }

        [Tooltip("Camera whose image is being cloned. Defaults to Camera.main (the AR camera).")]
        public Camera sourceCamera;

        public OffsetMode offsetMode = OffsetMode.AutoFromBounds;

        [Tooltip("Manual mode: screen-space offset in pixels. +X samples to the right.")]
        public Vector2 manualOffsetPixels = new Vector2(200f, 0f);

        [Tooltip("Auto mode: which side of the object to copy pixels from.")]
        public SamplingSide samplingSide = SamplingSide.AutoPickSide;

        [Tooltip("Auto mode: extra gap in pixels between the object's silhouette and the copied region.")]
        public float paddingPixels = 12f;

        [Tooltip("Auto mode: additional vertical offset in pixels. +Y samples upward.")]
        public float verticalOffsetPixels;

        [Range(0f, 1f)]
        [Tooltip("1 = fully cloned camera pixels, 0 = flat debug color.")]
        public float cloneBlend = 1f;

        [Range(0f, 1f)]
        public float alpha = 1f;

        static readonly int OffsetPixelsId = Shader.PropertyToID("_OffsetPixels");
        static readonly int BlendId = Shader.PropertyToID("_Blend");
        static readonly int AlphaId = Shader.PropertyToID("_Alpha");

        Renderer _renderer;
        MaterialPropertyBlock _block;

        void OnEnable()
        {
            _renderer = GetComponent<Renderer>();
            _block ??= new MaterialPropertyBlock();
        }

        void LateUpdate()
        {
            var cam = sourceCamera != null ? sourceCamera : Camera.main;
            if (cam == null || _renderer == null)
            {
                return;
            }

            Vector2 offset = offsetMode == OffsetMode.Manual
                ? manualOffsetPixels
                : ComputeAutoOffset(cam);

            _renderer.GetPropertyBlock(_block);
            _block.SetVector(OffsetPixelsId, new Vector4(offset.x, offset.y, 0f, 0f));
            _block.SetFloat(BlendId, cloneBlend);
            _block.SetFloat(AlphaId, alpha);
            _renderer.SetPropertyBlock(_block);
        }

        Vector2 ComputeAutoOffset(Camera cam)
        {
            GetScreenBounds(cam, _renderer.bounds, out float minX, out float maxX);

            float width = Mathf.Max(maxX - minX, 1f);
            float shift = width + paddingPixels;

            bool useRight = samplingSide switch
            {
                SamplingSide.Right => true,
                SamplingSide.Left => false,
                // Prefer the side that still has screen left over; if the patch would
                // run off the right edge, copy from the left instead.
                _ => maxX + shift <= cam.pixelWidth || minX - shift < 0f
            };

            return new Vector2(useRight ? shift : -shift, verticalOffsetPixels);
        }

        static void GetScreenBounds(Camera cam, Bounds bounds, out float minX, out float maxX)
        {
            minX = float.MaxValue;
            maxX = float.MinValue;

            Vector3 c = bounds.center;
            Vector3 e = bounds.extents;

            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    c.x + ((i & 1) == 0 ? -e.x : e.x),
                    c.y + ((i & 2) == 0 ? -e.y : e.y),
                    c.z + ((i & 4) == 0 ? -e.z : e.z));

                Vector3 sp = cam.WorldToScreenPoint(corner);

                // Corners behind the camera project mirrored; mirror them back so the
                // silhouette width stays sane while the object is partly off-screen.
                if (sp.z < 0f)
                {
                    sp.x = cam.pixelWidth - sp.x;
                }

                minX = Mathf.Min(minX, sp.x);
                maxX = Mathf.Max(maxX, sp.x);
            }
        }
    }
}
