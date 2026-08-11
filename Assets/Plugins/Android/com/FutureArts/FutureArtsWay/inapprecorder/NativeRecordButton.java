package com.FutureArts.FutureArtsWay.inapprecorder;

import android.app.Activity;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.util.Log;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;

import com.unity3d.player.UnityPlayer;

/**
 * A genuine native Android overlay for the in-app screen-record button and its progress ring,
 * added to the Activity's window alongside — never inside — Unity's own rendering surface.
 *
 * Why this exists: InAppScreenRecorder captures frames via
 * ScreenCapture.CaptureScreenshotIntoRenderTexture, which only ever reads back pixels that Unity
 * itself rendered into its own GL/Vulkan surface. It has no visibility into whatever the Android
 * window compositor layers on top from a separate native View — the same reason a web page's
 * MediaRecorder-of-a-&lt;canvas&gt; never captures an HTML element positioned outside that canvas
 * (see the FAWCurrents_WebARMain project's recording HUD, which is a plain DOM &lt;div&gt; appended
 * to document.body specifically so it's excluded from the canvas-based recording). A Unity uGUI
 * button, by contrast, IS rendered by Unity itself and therefore WOULD show up in the recording —
 * which is exactly the problem this class solves by moving the button out of Unity's own
 * rendering entirely, onto a real native Android View layered on top of Unity's surface instead.
 *
 * Position/size are supplied by the caller (see ScreenRecordButtonController.cs) derived from the
 * existing Unity RectTransform's on-screen pixel rect, so this stays visually in sync with
 * wherever the Unity button was already laid out without duplicating any layout logic natively.
 *
 * Caveat: this assumes Unity's own rendering surface is a normal (non "Z-order-on-top") View
 * within the Activity's window, so that a sibling View added afterward draws above it. This is
 * the common case for Unity's Android player, but if the button doesn't appear at all on-device,
 * Unity's surface may be forcing itself above all sibling views — in that case this needs to be
 * switched to a real separate WindowManager panel window instead of a sibling View.
 */
public class NativeRecordButton {
    private static final String TAG = "NativeRecordButton";

    private RingButtonView view;
    private String unityGameObjectName;

    /** Must be called on Unity's main thread; internally hops to the Android UI thread. */
    public void create(Activity activity, String gameObjectName, int x, int y, int width, int height) {
        if (activity == null) {
            Log.e(TAG, "create(): activity is null.");
            return;
        }
        unityGameObjectName = gameObjectName;
        activity.runOnUiThread(() -> {
            try {
                if (view != null) {
                    return; // already created
                }
                view = new RingButtonView(activity);
                view.setOnClickListener(v -> {
                    if (unityGameObjectName != null) {
                        UnityPlayer.UnitySendMessage(unityGameObjectName, "OnNativeButtonTapped", "");
                    }
                });

                FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(width, height);
                params.leftMargin = x;
                params.topMargin = y;
                view.setLayoutParams(params);

                ViewGroup content = activity.findViewById(android.R.id.content);
                if (content == null) {
                    Log.e(TAG, "create(): android.R.id.content not found — cannot attach the overlay.");
                    view = null;
                    return;
                }
                content.addView(view);
            } catch (Exception e) {
                Log.e(TAG, "create() failed", e);
                view = null;
            }
        });
    }

    public void show() {
        runOnView(v -> v.setVisibility(View.VISIBLE));
    }

    public void hide() {
        runOnView(v -> v.setVisibility(View.GONE));
    }

    public void setRecording(boolean recording) {
        runOnView(v -> v.setRecording(recording));
    }

    public void setFillAmount(float amount) {
        runOnView(v -> v.setFillAmount(amount));
    }

    public void destroy() {
        runOnView(v -> {
            ViewGroup parent = (ViewGroup) v.getParent();
            if (parent != null) {
                parent.removeView(v);
            }
        });
        view = null;
    }

    private interface ViewAction {
        void run(RingButtonView v);
    }

    private void runOnView(ViewAction action) {
        final RingButtonView v = view;
        if (v == null) {
            return;
        }
        v.post(() -> action.run(v));
    }

    /**
     * Draws the ring (radial progress, matching the Unity Image.Type.Filled / Radial360 look) and
     * the center icon (filled circle when idle, rounded square when recording) in one custom view.
     */
    private static class RingButtonView extends View {
        private static final float RING_STROKE_FRACTION = 0.09f; // relative to the shorter side
        private static final float ICON_FRACTION = 0.465f; // relative to the shorter side (~25% smaller than before)

        private final Paint ringPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint backgroundPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint iconPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final RectF ringRect = new RectF();
        private final RectF iconRect = new RectF();
        private float fillAmount = 0f;
        private boolean recording = false;

        RingButtonView(Activity activity) {
            super(activity);
            int red = Color.rgb(235, 45, 58); // matches ScreenRecordButtonRed.png / the ring art
            ringPaint.setStyle(Paint.Style.STROKE);
            ringPaint.setColor(red);
            backgroundPaint.setStyle(Paint.Style.FILL);
            backgroundPaint.setColor(Color.WHITE); // matches ScreenRecordButtonWhite.png
            iconPaint.setStyle(Paint.Style.FILL);
            iconPaint.setColor(red);
        }

        void setRecording(boolean isRecording) {
            this.recording = isRecording;
            invalidate();
        }

        void setFillAmount(float amount) {
            this.fillAmount = Math.max(0f, Math.min(1f, amount));
            invalidate();
        }

        @Override
        protected void onDraw(Canvas canvas) {
            super.onDraw(canvas);
            int w = getWidth();
            int h = getHeight();
            if (w <= 0 || h <= 0) {
                return;
            }

            float shortSide = Math.min(w, h);
            float strokeWidth = shortSide * RING_STROKE_FRACTION;
            ringPaint.setStrokeWidth(strokeWidth);
            float inset = strokeWidth / 2f;
            ringRect.set(inset, inset, w - inset, h - inset);

            if (fillAmount > 0f) {
                // Matches Unity's Radial360 fill with fillOrigin=Bottom, clockwise: starts at the
                // bottom (90 degrees in Android's drawArc convention, where 0 degrees is 3 o'clock)
                // and sweeps clockwise as fillAmount increases.
                canvas.drawArc(ringRect, 90f, 360f * fillAmount, false, ringPaint);
            }

            // White disc filling the inside of the ring, sitting just at its inner edge — matches
            // the original ScreenRecordButtonWhite.png background that used to sit behind the icon.
            float bgRadius = shortSide / 2f - strokeWidth;
            canvas.drawCircle(w / 2f, h / 2f, bgRadius, backgroundPaint);

            float iconSize = shortSide * ICON_FRACTION;
            float cx = w / 2f;
            float cy = h / 2f;
            iconRect.set(cx - iconSize / 2f, cy - iconSize / 2f, cx + iconSize / 2f, cy + iconSize / 2f);

            if (recording) {
                float cornerRadius = iconSize * 0.22f;
                canvas.drawRoundRect(iconRect, cornerRadius, cornerRadius, iconPaint);
            } else {
                canvas.drawOval(iconRect, iconPaint);
            }
        }
    }
}
