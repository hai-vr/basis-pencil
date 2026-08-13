TODO
=====

## Basis issues that may require a PR upstream

- Raycasts are not allowed in Cilbox Props.
- BasisCompression.QuaternionCompressor is not allowed in Cilbox Common.
  - Additionally, the fields of a Quaternion cannot be accessed, meaning we cannot compress it purely in Cilbox.
- It is unclear how to get the world-space eye position of the VR camera from a prop.
  - We need the world-space eye positions of the VR stereo camera; not the eye positions of the avatar, and not the position of the real eyes as if it were aligned to the avatar head.
