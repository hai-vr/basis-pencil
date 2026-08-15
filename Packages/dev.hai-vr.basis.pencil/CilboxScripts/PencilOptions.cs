using Basis;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Interactions;
using Basis.Scripts.Device_Management.Devices;
using UnityEngine;

namespace Hai.Basis.CilboxPencil
{
    [Cilboxable]
    [AddComponentMenu("HVR.Basis/Items/HVR Pencil Menu (Cilbox)")]
    public class PencilOptions : MonoBehaviour
    {
        private bool _isEnabled;

        public BasisPickupInteractable pencilPickup;
        public BasisPickupInteractable optionPickup;
        public LineRenderer optionLineRenderer;
        public Transform pivot;

        private Color[] _colors;
        private bool _isPickedUp;
        private BasisNetworkShim _network;
        private bool _networkReady;
        private bool _netIsPickedUp;

        public void Start()
        {
            optionPickup.OnInteractStartEvent.AddListener(WhenPickup);
            optionPickup.OnInteractEndEvent.AddListener(WhenDrop);
            optionPickup.OnPickupUse.AddListener(WhileUsing);

            _colors = PerceptualRainbow(8);

            _network = SafeUtil.MakeNetworkable(this);
            _network.NetworkMessageReceived += WhenNetworkMessageReceived;
            _network.NetworkReady += WhenNetworkReady;

            WhenEnable();
        }

        private void WhenNetworkReady()
        {
            _networkReady = true;
        }

        private void WhenNetworkMessageReceived(ushort playerID, byte[] buffer, DeliveryMethod deliveryMethod)
        {
            if (buffer.Length == 1)
            {
                var newNetIsPickedUp = buffer[0] == 1;
                if (newNetIsPickedUp != _netIsPickedUp)
                {
                    _netIsPickedUp = newNetIsPickedUp;
                    if (_netIsPickedUp) WhenPickup2();
                    else WhenDrop2();
                }
            }
        }

        private void WhenPickup(BasisInput arg0)
        {
            WhenPickup2();
            if (_networkReady) _network.SendCustomNetworkEvent(new byte[] { 1 }, DeliveryMethod.ReliableSequenced);
        }

        private void WhenDrop(BasisInput arg0)
        {
            WhenDrop2();
            if (_networkReady) _network.SendCustomNetworkEvent(new byte[] { 0 }, DeliveryMethod.ReliableSequenced);
        }

        private void WhenPickup2()
        {
            _isPickedUp = true;

            optionLineRenderer.gameObject.SetActive(true);
            DoUpdate();
        }

        private void WhenDrop2()
        {
            _isPickedUp = false;

            optionLineRenderer.gameObject.SetActive(false);
            optionPickup.transform.position = pivot.position;
        }

        private void WhileUsing(BasisPickUpUseMode useMode)
        {
        }

        private void Update()
        {
            if (_isPickedUp)
            {
                DoUpdate();
            }
        }

        private void DoUpdate()
        {
            pivot.rotation = Quaternion.LookRotation(optionPickup.transform.position - pivot.position, Vector3.up);
            optionLineRenderer.SetPosition(0, pivot.position);
            optionLineRenderer.SetPosition(1, optionPickup.transform.position);
        }

        public void OnEnable() { WhenEnable(); }
        private void OnDisable() { _isEnabled = false; }
        private void WhenEnable()
        {
            if (_isEnabled) return; // Cilbox quirk
            _isEnabled = true;

            optionLineRenderer.gameObject.SetActive(false);
        }

        private Color[] PerceptualRainbow(int count, float lightness = 0.75f, float chroma = 0.12f)
        {
            var colors = new Color[count];
            for (var i = 0; i < count; i++)
            {
                var hue = (i / (float)count) * Mathf.PI * 2f;

                var a = chroma * Mathf.Cos(hue);
                var b = chroma * Mathf.Sin(hue);

                colors[i] = OklabToRGB(new Vector3(lightness, a, b), 1.0f);
            }
            return colors;
        }

        private static Color OklabToRGB(Vector3 lab, float alpha)
        {
            var L = lab.x;
            var A = lab.y;
            var B = lab.z;

            var l = L + 0.3963377774f * A + 0.2158037573f * B;
            var m = L - 0.1055613458f * A - 0.0638541728f * B;
            var s = L - 0.0894841775f * A - 1.2914855480f * B;

            l = l * l * l;
            m = m * m * m;
            s = s * s * s;

            var r = 4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
            var g = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
            var b = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;

            return new Color(r, g, b, alpha);
        }
    }
}
