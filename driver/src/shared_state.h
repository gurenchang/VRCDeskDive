/*
 * VRCDeskDive アプリ <-> ドライバー間の共有メモリ。
 * レイアウトを変えたら VRCDESKDIVE_VERSION を上げ、Core/DriverLink.cs のオフセットも合わせること。
 */
#ifndef VRCDESKDIVE_SHARED_STATE_H
#define VRCDESKDIVE_SHARED_STATE_H

#include <stdint.h>

#define VRCDESKDIVE_SHM_NAME L"Local\\VRCDeskDive.Driver"
#define VRCDESKDIVE_MAGIC 0x44444356u /* 'VCDD' */
#define VRCDESKDIVE_VERSION 1u

/* アプリのハートビートがこれより古ければ、ドライバーは姿勢の変更をやめる */
#define VRCDESKDIVE_APP_TIMEOUT_MS 1500

typedef struct SharedState
{
    uint32_t magic;                     /*  0 */
    uint32_t version;                   /*  4 */

    /* ドライバー -> アプリ */
    volatile int64_t driverHeartbeat;   /*  8: GetTickCount64() */
    volatile uint32_t hookedCount;      /* 16: フックできた IVRServerDriverHost の数 */
    volatile uint32_t hmdPoseCount;     /* 20: HMD の姿勢更新を受け取った回数 */

    /* アプリ -> ドライバー */
    volatile int64_t appHeartbeat;      /* 24: GetTickCount64() */
    volatile int32_t enabled;           /* 32: 1 = デスクトップ操作中（姿勢を固定する） */
    volatile float pitchDeg;            /* 36: 水平からの上下角（上が正） */
    volatile int32_t lockPosition;      /* 40: 1 = 切り替え時の頭の位置に固定 */
    volatile uint32_t recaptureSeq;     /* 44: 値が変わったら基準を取り直す */
} SharedState;                          /* 48 bytes */

#endif
