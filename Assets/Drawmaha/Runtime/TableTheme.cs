using UnityEngine;

namespace Drawmaha
{
    public sealed class TableTheme : ScriptableObject
    {
        public string DisplayName = "Emerald";
        public Color Background = new Color(0.025f, 0.043f, 0.055f);
        public Color Felt = new Color(0.055f, 0.27f, 0.24f);
        public Color Rail = new Color(0.08f, 0.12f, 0.14f);
        public Color Accent = new Color(0.96f, 0.75f, 0.38f);
        public Color CardBack = new Color(0.09f, 0.23f, 0.30f);
        public Color CardFace = new Color(0.96f, 0.95f, 0.92f);
        public Texture2D BackgroundImage, TableImage, CardBackImage, CardFaceImage;
        [Tooltip("Optional profile pictures. Import with Read/Write enabled; masked to circles at runtime.")]
        public Texture2D[] Avatars = new Texture2D[0];
        [Range(0.05f, 1f)] public float CardDealSeconds = 0.14f;
        [Range(0.1f, 1f)] public float BoardDealSeconds = 0.38f;
    }
}
