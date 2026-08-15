// FawRecordButton.mm
//
// Native iOS overlay for the in-app screen-record button and its progress ring — the iOS
// counterpart to Assets/Plugins/Android/.../inapprecorder/NativeRecordButton.java.
//
// WHY A NATIVE OVERLAY (same reason as Android): recording goes through InAppScreenRecorder, which
// grabs frames via ScreenCapture.CaptureScreenshotIntoRenderTexture — i.e. it reads back only the
// pixels Unity itself rendered. A native UIView layered on TOP of Unity's Metal view is never part
// of Unity's own render output, so it can't appear in the recording. That's exactly what we want:
// the button stays visible to the user on the live display but is absent from the captured video.
//
// (This replaced an earlier ReplayKit-based approach. ReplayKit was a whole-screen system capture
// that DID include native overlays, which forced a fragile secureTextEntry content-protection hack
// to hide the button. Recording Unity's own frame instead makes that hack unnecessary — the button
// is simply a plain UIView now, just like the Android side.)

#import <UIKit/UIKit.h>

extern "C" void UnitySendMessage(const char *obj, const char *method, const char *msg);

static NSString *FawNSStringFromCString(const char *cstr) {
    return cstr ? [NSString stringWithUTF8String:cstr] : @"";
}

// ── Ring + icon drawing view ────────────────────────────────────────────────

@interface FawRingButtonView : UIView
@property(nonatomic, strong) CAShapeLayer *ringLayer;
@property(nonatomic, strong) CAShapeLayer *backgroundLayer;
@property(nonatomic, strong) CAShapeLayer *iconLayer;
@property(nonatomic, assign) BOOL recording;
@property(nonatomic, assign) CGFloat fillAmount;
- (void)refreshLayers;
@end

@implementation FawRingButtonView

- (instancetype)initWithFrame:(CGRect)frame {
    self = [super initWithFrame:frame];
    if (self) {
        self.backgroundColor = [UIColor clearColor];

        UIColor *red = [UIColor colorWithRed:235.0 / 255.0 green:45.0 / 255.0 blue:58.0 / 255.0 alpha:1.0];

        _ringLayer = [CAShapeLayer layer];
        _ringLayer.fillColor = [UIColor clearColor].CGColor;
        _ringLayer.strokeColor = red.CGColor;
        _ringLayer.strokeStart = 0.0;
        _ringLayer.strokeEnd = 0.0;
        [self.layer addSublayer:_ringLayer];

        // White disc filling the inside of the ring, sitting just at its inner edge — matches the
        // original ScreenRecordButtonWhite.png background that used to sit behind the icon.
        _backgroundLayer = [CAShapeLayer layer];
        _backgroundLayer.fillColor = [UIColor whiteColor].CGColor;
        [self.layer addSublayer:_backgroundLayer];

        _iconLayer = [CAShapeLayer layer];
        _iconLayer.fillColor = red.CGColor;
        [self.layer addSublayer:_iconLayer];

        _recording = NO;
        _fillAmount = 0.0;
    }
    return self;
}

- (void)layoutSubviews {
    [super layoutSubviews];
    [self refreshLayers];
}

- (void)setRecording:(BOOL)recording {
    _recording = recording;
    [self refreshLayers];
}

- (void)setFillAmount:(CGFloat)fillAmount {
    _fillAmount = MAX(0.0, MIN(1.0, fillAmount));
    _ringLayer.strokeEnd = _fillAmount;
}

- (void)refreshLayers {
    CGFloat w = self.bounds.size.width;
    CGFloat h = self.bounds.size.height;
    if (w <= 0 || h <= 0) return;

    CGFloat shortSide = MIN(w, h);
    CGFloat strokeWidth = shortSide * 0.09;
    _ringLayer.lineWidth = strokeWidth;
    _ringLayer.frame = self.bounds;

    CGFloat radius = shortSide / 2.0 - strokeWidth / 2.0;
    CGPoint center = CGPointMake(w / 2.0, h / 2.0);
    // Matches Unity's Radial360 fill with fillOrigin = Bottom, clockwise: the path starts at the
    // bottom (M_PI_2 in UIKit's Y-down coordinate space) and sweeps clockwise; strokeEnd reveals
    // the fraction of it that corresponds to fillAmount.
    UIBezierPath *ringPath = [UIBezierPath bezierPathWithArcCenter:center
                                                             radius:radius
                                                         startAngle:M_PI_2
                                                           endAngle:M_PI_2 + 2.0 * M_PI
                                                          clockwise:YES];
    _ringLayer.path = ringPath.CGPath;
    _ringLayer.strokeEnd = _fillAmount;

    CGFloat bgRadius = shortSide / 2.0 - strokeWidth;
    CGRect bgRect = CGRectMake(center.x - bgRadius, center.y - bgRadius, bgRadius * 2.0, bgRadius * 2.0);
    _backgroundLayer.frame = self.bounds;
    _backgroundLayer.path = [UIBezierPath bezierPathWithOvalInRect:bgRect].CGPath;

    CGFloat iconSize = shortSide * 0.465; // ~25% smaller than before
    CGRect iconRect = CGRectMake(center.x - iconSize / 2.0, center.y - iconSize / 2.0, iconSize, iconSize);
    _iconLayer.frame = self.bounds;

    UIBezierPath *iconPath;
    if (_recording) {
        CGFloat cornerRadius = iconSize * 0.22;
        iconPath = [UIBezierPath bezierPathWithRoundedRect:iconRect cornerRadius:cornerRadius];
    } else {
        iconPath = [UIBezierPath bezierPathWithOvalInRect:iconRect];
    }
    _iconLayer.path = iconPath.CGPath;
}

@end

// ── Overlay controller: draws the button and forwards taps ─────────────────

@interface FawRecordButtonOverlay : NSObject
@property(nonatomic, strong) FawRingButtonView *buttonView;
@property(nonatomic, copy) NSString *unityGameObjectName;
- (void)createWithGameObjectName:(NSString *)name
                               x:(int)x y:(int)y width:(int)width height:(int)height
                unityScreenWidth:(int)unityScreenWidth;
- (void)show;
- (void)hide;
- (void)setRecording:(BOOL)recording;
- (void)setFillAmount:(float)amount;
- (void)destroy;
- (void)handleTap;
@end

@implementation FawRecordButtonOverlay

static UIWindow *FawGetKeyWindow(void) {
    if (@available(iOS 13.0, *)) {
        for (UIScene *scene in [UIApplication sharedApplication].connectedScenes) {
            if (![scene isKindOfClass:[UIWindowScene class]]) continue;
            UIWindowScene *windowScene = (UIWindowScene *)scene;
            for (UIWindow *window in windowScene.windows) {
                if (window.isKeyWindow) return window;
            }
        }
    }
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
    return [UIApplication sharedApplication].keyWindow;
#pragma clang diagnostic pop
}

- (void)createWithGameObjectName:(NSString *)name
                               x:(int)x y:(int)y width:(int)width height:(int)height
                unityScreenWidth:(int)unityScreenWidth {
    self.unityGameObjectName = name;

    UIWindow *keyWindow = FawGetKeyWindow();
    if (keyWindow == nil) {
        NSLog(@"[FawRecordButton] createWithGameObjectName: no key window available — button will not appear.");
        return;
    }

    // The C# side measures the button's rect in device pixels (Unity's Screen.width space), but
    // UIKit frames are in points — on a 2x/3x Retina display those differ by the scale factor, so
    // using the raw values would draw the button 2-3x too large and in the wrong place. Derive the
    // pixel→point factor from the actual window width rather than [UIScreen scale], so it stays
    // correct even if Unity is rendering at a non-native resolution (Resolution Scaling settings),
    // where drawable pixels and screen.nativeScale no longer agree.
    CGFloat pixelToPoint = 1.0;
    if (unityScreenWidth > 0 && keyWindow.bounds.size.width > 0) {
        pixelToPoint = keyWindow.bounds.size.width / (CGFloat)unityScreenWidth;
    }
    CGRect frame = CGRectMake(x * pixelToPoint, y * pixelToPoint,
                              width * pixelToPoint, height * pixelToPoint);

    // A plain interactive overlay on top of Unity's view. InAppScreenRecorder captures only Unity's
    // own rendered frame, so this native view is never in the recording — no content-protection
    // trickery needed. It draws the button/ring and forwards taps back to Unity.
    self.buttonView = [[FawRingButtonView alloc] initWithFrame:frame];
    self.buttonView.backgroundColor = [UIColor clearColor];
    self.buttonView.userInteractionEnabled = YES;
    UITapGestureRecognizer *tap = [[UITapGestureRecognizer alloc] initWithTarget:self action:@selector(handleTap)];
    [self.buttonView addGestureRecognizer:tap];
    [keyWindow addSubview:self.buttonView];
    [keyWindow bringSubviewToFront:self.buttonView];
}

- (void)handleTap {
    if (self.unityGameObjectName != nil) {
        UnitySendMessage([self.unityGameObjectName UTF8String], "OnNativeButtonTapped", "");
    }
}

- (void)show {
    self.buttonView.hidden = NO;
}

- (void)hide {
    // A hidden view also drops out of hit-testing, so this is inert on the main menu.
    self.buttonView.hidden = YES;
}

- (void)setRecording:(BOOL)recording {
    self.buttonView.recording = recording;
}

- (void)setFillAmount:(float)amount {
    self.buttonView.fillAmount = amount;
}

- (void)destroy {
    [self.buttonView removeFromSuperview];
    self.buttonView = nil;
}

@end

// ── C bridge (called from ScreenRecordButtonController.cs via [DllImport("__Internal")]) ───────
//
// Unity's iOS player runs its main loop (and therefore all plugin calls made from C# Update/
// Start/etc.) on the main thread already, so these run the block directly when possible instead
// of always deferring via dispatch_async — that would add a needless one-runloop-tick delay to
// every call, which matters here since SetFillAmount is called once per rendered frame while the
// ring animates. dispatch_sync is used only as a defensive fallback for the (unexpected) case of
// being called from a background thread, since dispatch_sync-to-main from the main thread itself
// would deadlock.

static void FawRunOnMain(void (^block)(void)) {
    if ([NSThread isMainThread]) {
        block();
    } else {
        dispatch_sync(dispatch_get_main_queue(), block);
    }
}

static FawRecordButtonOverlay *gFawRecordButtonOverlay = nil;

extern "C" {

// x/y/width/height and unityScreenWidth are all in Unity's device-pixel screen space (top-left
// origin — the C# side already flips Y); the conversion to UIKit points happens inside
// createWithGameObjectName using unityScreenWidth as the reference.
void FawRecordButton_Create(const char *gameObjectName, int x, int y, int width, int height,
                            int unityScreenWidth) {
    FawRunOnMain(^{
        if (gFawRecordButtonOverlay != nil) {
            return; // already created
        }
        gFawRecordButtonOverlay = [[FawRecordButtonOverlay alloc] init];
        [gFawRecordButtonOverlay createWithGameObjectName:FawNSStringFromCString(gameObjectName)
                                                         x:x y:y width:width height:height
                                          unityScreenWidth:unityScreenWidth];
    });
}

void FawRecordButton_Show(void) {
    FawRunOnMain(^{
        [gFawRecordButtonOverlay show];
    });
}

void FawRecordButton_Hide(void) {
    FawRunOnMain(^{
        [gFawRecordButtonOverlay hide];
    });
}

void FawRecordButton_SetRecording(BOOL recording) {
    FawRunOnMain(^{
        [gFawRecordButtonOverlay setRecording:recording];
    });
}

void FawRecordButton_SetFillAmount(float amount) {
    FawRunOnMain(^{
        [gFawRecordButtonOverlay setFillAmount:amount];
    });
}

void FawRecordButton_Destroy(void) {
    FawRunOnMain(^{
        [gFawRecordButtonOverlay destroy];
        gFawRecordButtonOverlay = nil;
    });
}

} // extern "C"
