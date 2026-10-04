/*
 * VRCDeskDive SteamVR ドライバー
 *
 * 自分ではデバイスを追加せず、vrserver の IVRServerDriverHost::TrackedDevicePoseUpdated を
 * フックして HMD の姿勢だけを書き換える。デスクトップ操作中は、切り替えた瞬間の頭の位置と
 * 向き（左右）を保ったまま視点を水平にし、アプリから受け取った上下角を加える。
 * 無効にすれば HMD 本来の姿勢がそのまま流れるので、VR に戻したときにずれは残らない。
 *
 * C++ ABI に依存しないよう、OpenVR のインターフェースは関数ポインタ表として C で扱う
 * （MSVC x64 の仮想関数呼び出しは「this を第 1 引数に取る関数ポインタ表」と同じ）。
 * 構造体と関数の並びは ThirdParty/OpenVR/openvr_driver.h に合わせている。
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <math.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "shared_state.h"

#define EXPORT __declspec(dllexport)

/* ---- OpenVR の型（openvr_driver.h と同じレイアウト） ---- */

typedef struct Quat { double w, x, y, z; } Quat;
typedef struct Vec3 { double x, y, z; } Vec3;

typedef struct DriverPose
{
    double poseTimeOffset;
    Quat qWorldFromDriverRotation;
    double vecWorldFromDriverTranslation[3];
    Quat qDriverFromHeadRotation;
    double vecDriverFromHeadTranslation[3];
    double vecPosition[3];
    double vecVelocity[3];
    double vecAcceleration[3];
    Quat qRotation;
    double vecAngularVelocity[3];
    double vecAngularAcceleration[3];
    int32_t result;
    bool poseIsValid;
    bool willDriftInYaw;
    bool shouldApplyHeadModel;
    bool deviceIsConnected;
} DriverPose;

enum { VRInitError_None = 0, VRInitError_Init_InterfaceNotFound = 105 };
enum { TrackedDeviceIndex_Hmd = 0 };
enum { HostVtbl_TrackedDevicePoseUpdated = 1 };

typedef struct DriverContextVtbl
{
    void *(*GetGenericInterface)(void *self, const char *version, int32_t *error);
    uint64_t (*GetDriverHandle)(void *self);
} DriverContextVtbl;
typedef struct DriverContext { const DriverContextVtbl *vtbl; } DriverContext;

typedef struct DriverLogVtbl { void (*Log)(void *self, const char *message); } DriverLogVtbl;
typedef struct DriverLog { const DriverLogVtbl *vtbl; } DriverLog;

typedef void (*PoseUpdatedFn)(void *self, uint32_t device, const DriverPose *pose, uint32_t poseSize);

typedef struct ProviderVtbl
{
    int32_t (*Init)(void *self, DriverContext *context);
    void (*Cleanup)(void *self);
    const char *const *(*GetInterfaceVersions)(void *self);
    void (*RunFrame)(void *self);
    bool (*ShouldBlockStandbyMode)(void *self);
    void (*EnterStandby)(void *self);
    void (*LeaveStandby)(void *self);
} ProviderVtbl;
typedef struct Provider { const ProviderVtbl *vtbl; } Provider;

/* ---- 状態 ---- */

static DriverLog *g_log;
static HANDLE g_mapping;
static SharedState *g_shared;

/* フック中の姿勢固定の基準（HMD のスレッドからのみ触る） */
static bool g_captured;
static uint32_t g_capturedSeq;
static Vec3 g_targetPos;
static double g_targetYaw;

static void Log(const char *fmt, ...)
{
    char buf[512];
    va_list args;
    va_start(args, fmt);
    vsnprintf(buf, sizeof buf, fmt, args);
    va_end(args);
    if (g_log) g_log->vtbl->Log(g_log, buf);
}

/* ---- 数学 ---- */

static Quat QMul(Quat a, Quat b)
{
    Quat r = {
        a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z,
        a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
        a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
        a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
    };
    return r;
}

static Quat QConj(Quat q) { Quat r = { q.w, -q.x, -q.y, -q.z }; return r; }

static Vec3 QRotate(Quat q, Vec3 v)
{
    Quat p = { 0, v.x, v.y, v.z };
    Quat r = QMul(QMul(q, p), QConj(q));
    Vec3 out = { r.x, r.y, r.z };
    return out;
}

static Vec3 VAdd(Vec3 a, Vec3 b) { Vec3 r = { a.x + b.x, a.y + b.y, a.z + b.z }; return r; }
static Vec3 VSub(Vec3 a, Vec3 b) { Vec3 r = { a.x - b.x, a.y - b.y, a.z - b.z }; return r; }
static Vec3 VFrom(const double v[3]) { Vec3 r = { v[0], v[1], v[2] }; return r; }
static void VStore(double out[3], Vec3 v) { out[0] = v.x; out[1] = v.y; out[2] = v.z; }

static Quat QAxisAngle(double ax, double ay, double az, double angle)
{
    double s = sin(angle / 2);
    Quat q = { cos(angle / 2), ax * s, ay * s, az * s };
    return q;
}

/* 頭の向いている左右の角度。真上・真下を向いているときは頭頂方向から求める */
static double YawOf(Quat rot)
{
    Vec3 fwd = QRotate(rot, (Vec3){ 0, 0, -1 });
    Vec3 dir = fwd;
    if (sqrt(fwd.x * fwd.x + fwd.z * fwd.z) < 0.3)
    {
        Vec3 up = QRotate(rot, (Vec3){ 0, 1, 0 });
        dir = fwd.y < 0 ? up : (Vec3){ -up.x, -up.y, -up.z };
    }
    /* Y 軸まわりに yaw 回すと (0,0,-1) は (-sin, 0, -cos) になる */
    return atan2(-dir.x, -dir.z);
}

/*
 * 姿勢を固定した DriverPose を作る。
 * 頭のワールド姿勢 (rotW, posW) を目標 (targetRot, targetPos) に移す剛体変換 T を求め、
 * WorldFromDriver の前に掛ける。ドライバー空間の値はそのままなので矛盾しない。
 */
static void ApplyLock(DriverPose *p, const SharedState *s)
{
    Quat wfd = p->qWorldFromDriverRotation;
    Vec3 twfd = VFrom(p->vecWorldFromDriverTranslation);

    Vec3 headInDriver = VAdd(VFrom(p->vecPosition), QRotate(p->qRotation, VFrom(p->vecDriverFromHeadTranslation)));
    Quat rotW = QMul(wfd, QMul(p->qRotation, p->qDriverFromHeadRotation));
    Vec3 posW = VAdd(twfd, QRotate(wfd, headInDriver));

    if (!g_captured || g_capturedSeq != s->recaptureSeq)
    {
        g_captured = true;
        g_capturedSeq = s->recaptureSeq;
        g_targetPos = posW;
        g_targetYaw = YawOf(rotW);
        Log("[VRCDeskDive] captured pos=(%.3f, %.3f, %.3f) yaw=%.1f",
            posW.x, posW.y, posW.z, g_targetYaw * 180.0 / 3.14159265358979);
    }

    double pitch = s->pitchDeg * 3.14159265358979 / 180.0;
    /* Y 軸まわりの正の回転は左向きなので、右が正の yawDeg は引く */
    double yaw = g_targetYaw - s->yawDeg * 3.14159265358979 / 180.0;
    Quat targetRot = QMul(QAxisAngle(0, 1, 0, yaw), QAxisAngle(1, 0, 0, pitch));
    Vec3 targetPos = s->lockPosition ? g_targetPos : posW;

    Quat r = QMul(targetRot, QConj(rotW));
    Vec3 t = VSub(targetPos, QRotate(r, posW));

    p->qWorldFromDriverRotation = QMul(r, wfd);
    VStore(p->vecWorldFromDriverTranslation, VAdd(QRotate(r, twfd), t));

    /* 固定した姿勢を予測で動かさないよう、速度類は捨てる */
    memset(p->vecVelocity, 0, sizeof p->vecVelocity);
    memset(p->vecAcceleration, 0, sizeof p->vecAcceleration);
    memset(p->vecAngularVelocity, 0, sizeof p->vecAngularVelocity);
    memset(p->vecAngularAcceleration, 0, sizeof p->vecAngularAcceleration);
}

static bool IsActive(void)
{
    const SharedState *s = g_shared;
    if (!s || !s->enabled) return false;
    return (int64_t)GetTickCount64() - s->appHeartbeat < VRCDESKDIVE_APP_TIMEOUT_MS;
}

/* 診断用: どの経路で何が届いているか（RunFrame で定期的にログに出す） */
static volatile LONG g_callCount[3];
static volatile LONG g_lastDevice = -1;
static volatile LONG g_lastSize;
static volatile LONG g_minDevice = 0x7fffffff;

/* DriverPose はバージョンで後ろに項目が増えることがあるので、渡された大きさごとコピーする */
#define MAX_POSE_SIZE 1024

static void HandlePose(int hookIndex, PoseUpdatedFn original, void *self, uint32_t device, const DriverPose *pose, uint32_t size)
{
    InterlockedIncrement(&g_callCount[hookIndex]);
    g_lastDevice = (LONG)device;
    g_lastSize = (LONG)size;
    if ((LONG)device < g_minDevice) g_minDevice = (LONG)device;

    if (device != TrackedDeviceIndex_Hmd || size < sizeof(DriverPose) || size > MAX_POSE_SIZE)
    {
        original(self, device, pose, size);
        return;
    }
    if (g_shared) g_shared->hmdPoseCount++;

    if (!IsActive() || !pose->poseIsValid)
    {
        g_captured = false;
        original(self, device, pose, size);
        return;
    }

    _Alignas(16) unsigned char buffer[MAX_POSE_SIZE];
    memcpy(buffer, pose, size);
    ApplyLock((DriverPose *)buffer, g_shared);
    original(self, device, (const DriverPose *)buffer, size);
}

/* ---- vtable フック（インターフェースのバージョンごとに元の関数を持つ） ---- */

typedef struct HookSlot
{
    const char *version;
    void **slot;
    PoseUpdatedFn original;
    PoseUpdatedFn hook;
} HookSlot;

static void Hook004(void *self, uint32_t d, const DriverPose *p, uint32_t s);
static void Hook005(void *self, uint32_t d, const DriverPose *p, uint32_t s);
static void Hook006(void *self, uint32_t d, const DriverPose *p, uint32_t s);

static HookSlot g_hooks[] = {
    { "IVRServerDriverHost_004", NULL, NULL, Hook004 },
    { "IVRServerDriverHost_005", NULL, NULL, Hook005 },
    { "IVRServerDriverHost_006", NULL, NULL, Hook006 },
};
#define HOOK_COUNT (sizeof g_hooks / sizeof g_hooks[0])

static void Hook004(void *self, uint32_t d, const DriverPose *p, uint32_t s) { HandlePose(0, g_hooks[0].original, self, d, p, s); }
static void Hook005(void *self, uint32_t d, const DriverPose *p, uint32_t s) { HandlePose(1, g_hooks[1].original, self, d, p, s); }
static void Hook006(void *self, uint32_t d, const DriverPose *p, uint32_t s) { HandlePose(2, g_hooks[2].original, self, d, p, s); }

static bool IsOurHook(void *fn)
{
    for (size_t i = 0; i < HOOK_COUNT; i++)
        if (fn == (void *)g_hooks[i].hook) return true;
    return false;
}

static bool WriteSlot(void **slot, void *value)
{
    DWORD old;
    if (!VirtualProtect(slot, sizeof(void *), PAGE_READWRITE, &old)) return false;
    *slot = value;
    VirtualProtect(slot, sizeof(void *), old, &old);
    return true;
}

static uint32_t InstallHooks(DriverContext *context)
{
    uint32_t count = 0;
    for (size_t i = 0; i < HOOK_COUNT; i++)
    {
        HookSlot *h = &g_hooks[i];
        int32_t err = 0;
        void **host = (void **)context->vtbl->GetGenericInterface(context, h->version, &err);
        if (!host || err != 0) continue;

        void **slot = &((void **)*host)[HostVtbl_TrackedDevicePoseUpdated];
        /* 別バージョンと同じ vtable を共有している場合は二重にフックしない */
        if (IsOurHook(*slot)) continue;

        PoseUpdatedFn original = (PoseUpdatedFn)*slot;
        if (!WriteSlot(slot, (void *)h->hook)) continue;
        h->slot = slot;
        h->original = original;
        count++;
        Log("[VRCDeskDive] hooked %s", h->version);
    }
    return count;
}

static void RemoveHooks(void)
{
    for (size_t i = 0; i < HOOK_COUNT; i++)
    {
        HookSlot *h = &g_hooks[i];
        if (h->slot && *h->slot == (void *)h->hook) WriteSlot(h->slot, (void *)h->original);
        h->slot = NULL;
    }
}

/* ---- 共有メモリ ---- */

static void OpenShared(void)
{
    g_mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, sizeof(SharedState), VRCDESKDIVE_SHM_NAME);
    if (!g_mapping) return;
    g_shared = (SharedState *)MapViewOfFile(g_mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SharedState));
    if (!g_shared) return;
    g_shared->magic = VRCDESKDIVE_MAGIC;
    g_shared->version = VRCDESKDIVE_VERSION;
}

static void CloseShared(void)
{
    if (g_shared)
    {
        g_shared->driverHeartbeat = 0;
        UnmapViewOfFile(g_shared);
    }
    if (g_mapping) CloseHandle(g_mapping);
    g_shared = NULL;
    g_mapping = NULL;
}

/* ---- IServerTrackedDeviceProvider ---- */

static const char *const k_InterfaceVersions[] = {
    "IVRSettings_003",
    "ITrackedDeviceServerDriver_005",
    "IVRDisplayComponent_003",
    "IVRDriverDirectModeComponent_009",
    "IVRCameraComponent_003",
    "IServerTrackedDeviceProvider_004",
    "IVRWatchdogProvider_001",
    "IVRVirtualDisplay_002",
    "IVRDriverManager_001",
    "IVRResources_001",
    "IVRCompositorPluginProvider_001",
    "IVRIPCResourceManagerClient_003",
    NULL,
};

static int32_t ProviderInit(void *self, DriverContext *context)
{
    (void)self;
    int32_t err = 0;
    g_log = (DriverLog *)context->vtbl->GetGenericInterface(context, "IVRDriverLog_001", &err);

    OpenShared();
    uint32_t hooked = InstallHooks(context);
    if (g_shared) g_shared->hookedCount = hooked;
    Log("[VRCDeskDive] init: hooks=%u shared=%s", hooked, g_shared ? "ok" : "failed");
    return VRInitError_None;
}

static void ProviderCleanup(void *self)
{
    (void)self;
    RemoveHooks();
    CloseShared();
    g_log = NULL;
}

static const char *const *ProviderGetInterfaceVersions(void *self) { (void)self; return k_InterfaceVersions; }

static void ProviderRunFrame(void *self)
{
    (void)self;
    uint64_t now = GetTickCount64();
    if (g_shared) g_shared->driverHeartbeat = (int64_t)now;

    /* 5 秒ごとに、フックを通った姿勢更新の状況をログに出す */
    static uint64_t nextStats;
    if (now >= nextStats)
    {
        nextStats = now + 5000;
        Log("[VRCDeskDive] stats calls(v004=%ld v005=%ld v006=%ld) hmd=%u lastDevice=%ld minDevice=%ld lastSize=%ld expected=%u",
            g_callCount[0], g_callCount[1], g_callCount[2],
            g_shared ? g_shared->hmdPoseCount : 0u,
            g_lastDevice, g_minDevice, g_lastSize, (unsigned)sizeof(DriverPose));
    }
}

static bool ProviderShouldBlockStandbyMode(void *self) { (void)self; return false; }
static void ProviderEnterStandby(void *self) { (void)self; }
static void ProviderLeaveStandby(void *self) { (void)self; }

static const ProviderVtbl g_providerVtbl = {
    ProviderInit,
    ProviderCleanup,
    ProviderGetInterfaceVersions,
    ProviderRunFrame,
    ProviderShouldBlockStandbyMode,
    ProviderEnterStandby,
    ProviderLeaveStandby,
};
static Provider g_provider = { &g_providerVtbl };

EXPORT void *HmdDriverFactory(const char *interfaceName, int32_t *returnCode)
{
    if (strcmp(interfaceName, "IServerTrackedDeviceProvider_004") == 0) return &g_provider;
    if (returnCode) *returnCode = VRInitError_Init_InterfaceNotFound;
    return NULL;
}
