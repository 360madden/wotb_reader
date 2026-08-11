namespace WotBTreader.Core.Discovery;

/// <summary>
/// Hash-bound x86 layout for reading the live replay camera pose through the
/// CAM-001 fixed member-path: anchor the avatar object by its vftable dword
/// (runtime module base + RVA), hop through BattleResources and the camera
/// controller, then read the pose region of the GameCamera. Every hop is
/// identity-gated against the exact 11.19.0.10 executable.
/// </summary>
/// <remarks>
/// Live-verified 2026-08-11 (CAM-002/CAM-004): both identity gates pass on
/// the real process (ReplayCameraController vftable base+0x326dd0c,
/// GameCamera base+0x32dafa0), the GameCamera carries the live pose
/// (position +0x38, yaw cos/sin +0x50/+0x54, pitch +0x58, view basis
/// +0x80..0xA8), and posA is the true world camera (23.57 m third-person
/// offset from the viewpoint tank, 7/8 rounds, `camera-state-consistent`).
/// The chain is deliberately gate-free with respect to the session
/// controller: the CAM-003 finding is that the session-controller vftable
/// flips between launches (base+0x325ad2c vs base+0x323d9bc), but the
/// avatar/camera objects are stable across both phases.
/// </remarks>
public sealed record Type10CameraPoseLayout(
    string GameVersion,
    string ExecutableSha256,
    uint AvatarVftableReplayRva,
    uint AvatarVftableLiveRva,
    uint AvatarBattleResourcesOffset,
    uint CameraControllerOffset,
    uint CameraReplayVftableRva,
    uint CameraLiveVftableRva,
    uint CameraStateOffset,
    uint CameraStateVftableRva,
    uint PositionOffset,
    uint YawCosOffset,
    uint YawSinOffset,
    uint PitchOffset,
    uint BasisOffset,
    int PoseRegionLength,
    int MaxCandidates,
    long MinRegionSize)
{
    /// <summary>Static layout verified for the exact 11.19.0.10 executable.</summary>
    public static Type10CameraPoseLayout WotBlitz1119010 { get; } = new(
        GameVersion: "11.19.0.10",
        ExecutableSha256: "1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d",
        AvatarVftableReplayRva: 0x03277e8c,
        AvatarVftableLiveRva: 0x03277da4,
        AvatarBattleResourcesOffset: 0x154,
        CameraControllerOffset: 0x2c,
        CameraReplayVftableRva: 0x0326dd0c,
        CameraLiveVftableRva: 0x032de028,
        CameraStateOffset: 0x28,
        CameraStateVftableRva: 0x032dafa0,
        PositionOffset: 0x38,
        YawCosOffset: 0x50,
        YawSinOffset: 0x54,
        PitchOffset: 0x58,
        BasisOffset: 0x80,
        PoseRegionLength: 0x78, // covers +0x38..+0xB0 (basis ends at +0xA8)
        MaxCandidates: 4,
        MinRegionSize: 4096);
}
