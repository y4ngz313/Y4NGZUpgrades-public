using UnityEngine;

namespace cakeslice
{
    [RequireComponent(typeof(Renderer))]
    public sealed class Outline : MonoBehaviour
    {
        private static readonly Material[] EmptyMaterials = new Material[0];

        private Material[] _sharedMaterials;

        public Renderer Renderer { get; private set; }
        public SpriteRenderer SpriteRenderer { get; private set; }
        public SkinnedMeshRenderer SkinnedMeshRenderer { get; private set; }
        public MeshFilter MeshFilter { get; private set; }

        public int color;
        public bool eraseRenderer;

        public Material[] SharedMaterials
        {
            get
            {
                if (_sharedMaterials == null && Renderer != null)
                    _sharedMaterials = Renderer.sharedMaterials;

                return _sharedMaterials ?? EmptyMaterials;
            }
        }

        private void Awake()
        {
            Renderer = GetComponent<Renderer>();
            SkinnedMeshRenderer = GetComponent<SkinnedMeshRenderer>();
            SpriteRenderer = GetComponent<SpriteRenderer>();
            MeshFilter = GetComponent<MeshFilter>();
        }

        private void OnEnable()
        {
            OutlineEffect.Instance?.AddOutline(this);
        }

        private void OnDisable()
        {
            OutlineEffect.Instance?.RemoveOutline(this);
        }
    }
}
