// InAppFrameEncoder.mm
//
// iOS counterpart to InAppFrameEncoder.java. An AVAssetWriter-based H.264 + AAC encoder that
// accepts tightly-packed I420 video frames and interleaved int16 PCM audio from
// InAppScreenRecorder.cs — the SAME cross-platform Unity-frame capture pipeline Android uses —
// and muxes them into an mp4.
//
// WHY THIS EXISTS: iOS used to record via ReplayKit (a whole-screen system capture), which has no
// way to exclude the in-app record button from the video, forcing a fragile secureTextEntry hack.
// By encoding Unity's OWN rendered frames (ScreenCapture.CaptureScreenshotIntoRenderTexture) here
// instead, native overlays like the record button are never in the captured frames at all — the
// same reason Android's InAppFrameEncoder never captures its native button. See InAppScreenRecorder.cs.
//
// Exposed as extern "C" for [DllImport("__Internal")]. One active recording at a time (a single
// global session). All Configure/Push/Stop calls arrive serialized from InAppScreenRecorder's
// single encode thread, and readyForMoreMediaData is checked before every append, so no extra
// locking is needed beyond what AVAssetWriter already provides.

#import <Foundation/Foundation.h>
#import <AVFoundation/AVFoundation.h>
#import <CoreVideo/CoreVideo.h>
#import <CoreMedia/CoreMedia.h>
#import <Photos/Photos.h>
#import <UIKit/UIKit.h>

static NSString *IAFEStr(const char *cstr) {
    return cstr ? [NSString stringWithUTF8String:cstr] : nil;
}

// ── Session ─────────────────────────────────────────────────────────────────

@interface IAFEncoderSession : NSObject
@property(nonatomic, strong) AVAssetWriter *writer;
@property(nonatomic, strong) AVAssetWriterInput *videoInput;
@property(nonatomic, strong) AVAssetWriterInputPixelBufferAdaptor *pixelAdaptor;
@property(nonatomic, strong) AVAssetWriterInput *audioInput;
@property(nonatomic, assign) int width;
@property(nonatomic, assign) int height;
@property(nonatomic, assign) int audioSampleRate;
@property(nonatomic, assign) int audioChannels;
@property(nonatomic, assign) CMAudioFormatDescriptionRef audioFormat;
@end

@implementation IAFEncoderSession
- (void)dealloc {
    if (_audioFormat) { CFRelease(_audioFormat); _audioFormat = NULL; }
}
@end

static IAFEncoderSession *gSession = nil;

// Copies one tightly-packed source plane (srcStride == plane width) into a CVPixelBuffer plane,
// respecting the pixel buffer's own per-row stride (which is usually padded wider than the plane).
static void IAFECopyPlane(CVPixelBufferRef pb, size_t planeIndex,
                          const uint8_t *src, size_t planeWidth, size_t planeHeight) {
    uint8_t *dst = (uint8_t *)CVPixelBufferGetBaseAddressOfPlane(pb, planeIndex);
    size_t dstStride = CVPixelBufferGetBytesPerRowOfPlane(pb, planeIndex);
    if (dstStride == planeWidth) {
        memcpy(dst, src, planeWidth * planeHeight);
        return;
    }
    for (size_t row = 0; row < planeHeight; row++) {
        memcpy(dst + row * dstStride, src + row * planeWidth, planeWidth);
    }
}

static UIViewController *IAFETopViewController(void) {
    UIWindow *keyWindow = nil;
    for (UIScene *scene in [UIApplication sharedApplication].connectedScenes) {
        if (![scene isKindOfClass:[UIWindowScene class]]) continue;
        for (UIWindow *w in ((UIWindowScene *)scene).windows) {
            if (w.isKeyWindow) { keyWindow = w; break; }
        }
        if (keyWindow) break;
    }
    UIViewController *vc = keyWindow.rootViewController;
    while (vc.presentedViewController) vc = vc.presentedViewController;
    return vc;
}

// ── C bridge ─────────────────────────────────────────────────────────────────

extern "C" {

bool IAFE_Configure(const char *outPath, int width, int height, int fps, int bitRate,
                    int audioSampleRate, int audioChannelCount) {
    // Unity builds iOS with Objective-C exceptions disabled (-fno-objc-exceptions), so this relies
    // on the explicit nil / NSError / OSStatus checks below instead of @try/@catch.
        if (gSession != nil) {
            NSLog(@"[InAppFrameEncoder] Configure called while a session is already active — ignoring.");
            return false;
        }
        NSString *path = IAFEStr(outPath);
        if (path.length == 0) {
            NSLog(@"[InAppFrameEncoder] Configure: empty output path.");
            return false;
        }
        [[NSFileManager defaultManager] removeItemAtPath:path error:nil];
        NSURL *url = [NSURL fileURLWithPath:path];

        NSError *err = nil;
        AVAssetWriter *writer = [[AVAssetWriter alloc] initWithURL:url fileType:AVFileTypeMPEG4 error:&err];
        if (writer == nil) {
            NSLog(@"[InAppFrameEncoder] Failed to create AVAssetWriter: %@", err);
            return false;
        }

        // Video: H.264 at the requested size/bitrate.
        NSDictionary *videoSettings = @{
            AVVideoCodecKey: AVVideoCodecTypeH264,
            AVVideoWidthKey: @(width),
            AVVideoHeightKey: @(height),
            AVVideoCompressionPropertiesKey: @{ AVVideoAverageBitRateKey: @(bitRate) },
        };
        AVAssetWriterInput *videoInput = [AVAssetWriterInput assetWriterInputWithMediaType:AVMediaTypeVideo
                                                                            outputSettings:videoSettings];
        videoInput.expectsMediaDataInRealTime = YES;

        // The source pixel buffers are I420 planar — matches the tightly-packed output of
        // InAppScreenRecorder.RgbaToI420 (which also already flips to top-to-bottom rows).
        NSDictionary *pbAttrs = @{
            (id)kCVPixelBufferPixelFormatTypeKey: @(kCVPixelFormatType_420YpCbCr8Planar),
            (id)kCVPixelBufferWidthKey: @(width),
            (id)kCVPixelBufferHeightKey: @(height),
            (id)kCVPixelBufferIOSurfacePropertiesKey: @{},
        };
        AVAssetWriterInputPixelBufferAdaptor *adaptor =
            [AVAssetWriterInputPixelBufferAdaptor assetWriterInputPixelBufferAdaptorWithAssetWriterInput:videoInput
                                                                        sourcePixelBufferAttributes:pbAttrs];
        if (![writer canAddInput:videoInput]) {
            NSLog(@"[InAppFrameEncoder] Cannot add video input.");
            return false;
        }
        [writer addInput:videoInput];

        AVAssetWriterInput *audioInput = nil;
        CMAudioFormatDescriptionRef audioFormat = NULL;
        if (audioChannelCount > 0 && audioSampleRate > 0) {
            NSDictionary *audioSettings = @{
                AVFormatIDKey: @(kAudioFormatMPEG4AAC),
                AVNumberOfChannelsKey: @(audioChannelCount),
                AVSampleRateKey: @(audioSampleRate),
                AVEncoderBitRateKey: @(128000),
            };
            audioInput = [AVAssetWriterInput assetWriterInputWithMediaType:AVMediaTypeAudio
                                                            outputSettings:audioSettings];
            audioInput.expectsMediaDataInRealTime = YES;
            if ([writer canAddInput:audioInput]) {
                [writer addInput:audioInput];

                // Source format: interleaved signed 16-bit PCM (what AudioCaptureTap produces).
                AudioStreamBasicDescription asbd = {0};
                asbd.mSampleRate = audioSampleRate;
                asbd.mFormatID = kAudioFormatLinearPCM;
                asbd.mFormatFlags = kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked;
                asbd.mFramesPerPacket = 1;
                asbd.mChannelsPerFrame = audioChannelCount;
                asbd.mBitsPerChannel = 16;
                asbd.mBytesPerFrame = audioChannelCount * sizeof(int16_t);
                asbd.mBytesPerPacket = asbd.mBytesPerFrame;
                OSStatus st = CMAudioFormatDescriptionCreate(kCFAllocatorDefault, &asbd, 0, NULL, 0, NULL, NULL, &audioFormat);
                if (st != noErr) {
                    NSLog(@"[InAppFrameEncoder] CMAudioFormatDescriptionCreate failed (%d) — recording video-only.", (int)st);
                    audioInput = nil;
                    audioFormat = NULL;
                }
            } else {
                NSLog(@"[InAppFrameEncoder] Cannot add audio input — recording video-only.");
                audioInput = nil;
            }
        }

        if (![writer startWriting]) {
            NSLog(@"[InAppFrameEncoder] startWriting failed: %@", writer.error);
            return false;
        }
        // PTS from InAppScreenRecorder are relative to record-start (>= 0), so the session origin
        // is zero.
        [writer startSessionAtSourceTime:kCMTimeZero];

        IAFEncoderSession *session = [IAFEncoderSession new];
        session.writer = writer;
        session.videoInput = videoInput;
        session.pixelAdaptor = adaptor;
        session.audioInput = audioInput;
        session.audioFormat = audioFormat;
        session.width = width;
        session.height = height;
        session.audioSampleRate = audioSampleRate;
        session.audioChannels = audioChannelCount;
        gSession = session;
        return true;
}

bool IAFE_PushFrame(const void *i420, int length, long long ptsUs) {
    IAFEncoderSession *session = gSession;
    if (session == nil || i420 == NULL) return false;
    if (session.writer.status != AVAssetWriterStatusWriting) return false;
    if (!session.videoInput.isReadyForMoreMediaData) return false; // backpressure — drop this frame

    int w = session.width, h = session.height;
    size_t ySize = (size_t)w * h;
    size_t cSize = (size_t)(w / 2) * (h / 2);
    if ((size_t)length < ySize + 2 * cSize) return false;

    CVPixelBufferRef pb = NULL;
    CVPixelBufferPoolRef pool = session.pixelAdaptor.pixelBufferPool;
    if (pool != NULL) {
        CVPixelBufferPoolCreatePixelBuffer(NULL, pool, &pb);
    }
    if (pb == NULL) {
        NSDictionary *attrs = @{ (id)kCVPixelBufferIOSurfacePropertiesKey: @{} };
        CVPixelBufferCreate(kCFAllocatorDefault, w, h, kCVPixelFormatType_420YpCbCr8Planar,
                            (__bridge CFDictionaryRef)attrs, &pb);
    }
    if (pb == NULL) return false;

    CVPixelBufferLockBaseAddress(pb, 0);
    const uint8_t *src = (const uint8_t *)i420;
    IAFECopyPlane(pb, 0, src, w, h);                          // Y
    IAFECopyPlane(pb, 1, src + ySize, w / 2, h / 2);          // Cb (U)
    IAFECopyPlane(pb, 2, src + ySize + cSize, w / 2, h / 2);  // Cr (V)
    CVPixelBufferUnlockBaseAddress(pb, 0);

    // BT.601 studio-swing — matches the integer coefficients in InAppScreenRecorder.RgbaToI420, so
    // playback colors don't come out shifted (greenish/washed) from a mismatched matrix assumption.
    CVBufferSetAttachment(pb, kCVImageBufferYCbCrMatrixKey, kCVImageBufferYCbCrMatrix_ITU_R_601_4, kCVAttachmentMode_ShouldPropagate);
    CVBufferSetAttachment(pb, kCVImageBufferColorPrimariesKey, kCVImageBufferColorPrimaries_SMPTE_C, kCVAttachmentMode_ShouldPropagate);
    CVBufferSetAttachment(pb, kCVImageBufferTransferFunctionKey, kCVImageBufferTransferFunction_ITU_R_709_2, kCVAttachmentMode_ShouldPropagate);

    CMTime pts = CMTimeMake(ptsUs, 1000000);
    BOOL ok = [session.pixelAdaptor appendPixelBuffer:pb withPresentationTime:pts];
    CVPixelBufferRelease(pb);
    if (!ok) {
        NSLog(@"[InAppFrameEncoder] appendPixelBuffer failed: %@", session.writer.error);
    }
    return ok;
}

bool IAFE_PushAudio(const void *pcm, int sampleCount, long long ptsUs) {
    IAFEncoderSession *session = gSession;
    if (session == nil || pcm == NULL || sampleCount <= 0) return false;
    if (session.audioInput == nil || session.audioFormat == NULL) return false;
    if (session.writer.status != AVAssetWriterStatusWriting) return false;
    if (!session.audioInput.isReadyForMoreMediaData) return false;

    int channels = session.audioChannels > 0 ? session.audioChannels : 1;
    int frames = sampleCount / channels;
    if (frames <= 0) return false;
    size_t dataSize = (size_t)sampleCount * sizeof(int16_t);

    CMBlockBufferRef blockBuffer = NULL;
    OSStatus st = CMBlockBufferCreateWithMemoryBlock(kCFAllocatorDefault, NULL, dataSize,
                                                     kCFAllocatorDefault, NULL, 0, dataSize, 0, &blockBuffer);
    if (st != noErr) return false;
    st = CMBlockBufferReplaceDataBytes(pcm, blockBuffer, 0, dataSize);
    if (st != noErr) { CFRelease(blockBuffer); return false; }

    CMSampleTimingInfo timing;
    timing.duration = CMTimeMake(1, session.audioSampleRate);
    timing.presentationTimeStamp = CMTimeMake(ptsUs, 1000000);
    timing.decodeTimeStamp = kCMTimeInvalid;

    CMSampleBufferRef sampleBuffer = NULL;
    st = CMSampleBufferCreate(kCFAllocatorDefault, blockBuffer, true, NULL, NULL,
                              session.audioFormat, frames, 1, &timing, 0, NULL, &sampleBuffer);
    CFRelease(blockBuffer);
    if (st != noErr || sampleBuffer == NULL) return false;

    BOOL ok = [session.audioInput appendSampleBuffer:sampleBuffer];
    CFRelease(sampleBuffer);
    if (!ok) {
        NSLog(@"[InAppFrameEncoder] appendSampleBuffer (audio) failed: %@", session.writer.error);
    }
    return ok;
}

bool IAFE_Stop(void) {
    IAFEncoderSession *session = gSession;
    gSession = nil;
    if (session == nil) return false;

    [session.videoInput markAsFinished];
    if (session.audioInput) [session.audioInput markAsFinished];

    dispatch_semaphore_t sem = dispatch_semaphore_create(0);
    __block BOOL success = NO;
    [session.writer finishWritingWithCompletionHandler:^{
        success = (session.writer.status == AVAssetWriterStatusCompleted);
        if (!success) {
            NSLog(@"[InAppFrameEncoder] finishWriting ended with status %ld, error: %@",
                  (long)session.writer.status, session.writer.error);
        }
        dispatch_semaphore_signal(sem);
    }];
    // finishWriting runs on AVFoundation's own queue (not the main runloop), so blocking the
    // caller here is safe even when called from the main thread.
    dispatch_semaphore_wait(sem, DISPATCH_TIME_FOREVER);
    return success;
}

void IAFE_SaveToPhotos(const char *path) {
    NSString *p = IAFEStr(path);
    if (p.length == 0) return;
    NSURL *url = [NSURL fileURLWithPath:p];
    void (^save)(void) = ^{
        [[PHPhotoLibrary sharedPhotoLibrary] performChanges:^{
            [PHAssetChangeRequest creationRequestForAssetFromVideoAtFileURL:url];
        } completionHandler:^(BOOL ok, NSError *err) {
            if (!ok) NSLog(@"[InAppFrameEncoder] Save to Photos failed: %@", err);
        }];
    };
    // Add-only authorization is enough to create an asset; request it first so the very first
    // recording still lands in the camera roll.
    if (@available(iOS 14, *)) {
        [PHPhotoLibrary requestAuthorizationForAccessLevel:PHAccessLevelAddOnly
                                                   handler:^(PHAuthorizationStatus status) { save(); }];
    } else {
        [PHPhotoLibrary requestAuthorization:^(PHAuthorizationStatus status) { save(); }];
    }
}

void IAFE_ShareVideo(const char *path, const char *title) {
    NSString *p = IAFEStr(path);
    if (p.length == 0) return;
    NSURL *url = [NSURL fileURLWithPath:p];
    dispatch_async(dispatch_get_main_queue(), ^{
        UIActivityViewController *vc = [[UIActivityViewController alloc] initWithActivityItems:@[url]
                                                                        applicationActivities:nil];
        UIViewController *host = IAFETopViewController();
        if (host == nil) {
            NSLog(@"[InAppFrameEncoder] ShareVideo: no host view controller.");
            return;
        }
        // iPad requires a popover anchor or it throws.
        if (vc.popoverPresentationController) {
            vc.popoverPresentationController.sourceView = host.view;
            vc.popoverPresentationController.sourceRect = CGRectMake(host.view.bounds.size.width / 2.0,
                                                                     host.view.bounds.size.height, 1, 1);
        }
        [host presentViewController:vc animated:YES completion:nil];
    });
}

} // extern "C"
