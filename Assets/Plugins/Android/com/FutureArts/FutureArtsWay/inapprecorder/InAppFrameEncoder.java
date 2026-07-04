package com.FutureArts.FutureArtsWay.inapprecorder;

import android.media.Image;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.MediaMuxer;
import android.util.Log;

import java.nio.ByteBuffer;

/**
 * Minimal on-device H.264 encoder that takes raw I420 (planar YUV420) frames pushed from Unity
 * and muxes them into an mp4 file.
 *
 * Deliberately does NOT use Android's MediaProjection screen-capture broker. Frames are supplied
 * by Unity itself (captured on the C# side via ScreenCapture.CaptureScreenshotIntoRenderTexture +
 * AsyncGPUReadback — i.e. Unity's own already-rendered pixels, not a mirror of the OS display
 * compositor), so there is no "Share your screen with this app?" system consent dialog: the app
 * is only encoding pixels it already produced itself.
 *
 * Video only in this first version — no audio track.
 *
 * Threading: all public methods are called from Unity's main thread, one at a time, synchronously
 * (no internal background thread). synchronized is used defensively in case that ever changes.
 */
public class InAppFrameEncoder {
    private static final String TAG = "InAppFrameEncoder";
    private static final String MIME_TYPE = "video/avc";
    private static final int I_FRAME_INTERVAL_SECONDS = 2;
    private static final long DEQUEUE_TIMEOUT_US = 10_000; // 10ms
    private static final long DEQUEUE_TIMEOUT_US_EOS = 250_000; // 250ms, only used while draining at stop()

    private MediaCodec encoder;
    private MediaMuxer muxer;
    private MediaCodec.BufferInfo bufferInfo;
    private int trackIndex = -1;
    private boolean muxerStarted = false;
    private int frameWidth;
    private int frameHeight;
    private String outputPath;
    private long pushFrameCallCount = 0;
    private long samplesWrittenCount = 0;
    private boolean swapUAndVPlanes = true;

    /** Returns true on success. Logs and returns false (with cleanup already done) on failure. */
    public synchronized boolean configure(String outPath, int width, int height, int frameRate, int bitRate,
            boolean swapUV) {
        try {
            outputPath = outPath;
            frameWidth = width;
            frameHeight = height;
            swapUAndVPlanes = swapUV;
            bufferInfo = new MediaCodec.BufferInfo();
            trackIndex = -1;
            muxerStarted = false;

            MediaFormat format = MediaFormat.createVideoFormat(MIME_TYPE, width, height);
            format.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible);
            format.setInteger(MediaFormat.KEY_BIT_RATE, bitRate);
            format.setInteger(MediaFormat.KEY_FRAME_RATE, frameRate);
            format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, I_FRAME_INTERVAL_SECONDS);
            // Explicitly signal the color space we're actually encoding (limited-range BT.601 —
            // matches the studio-swing 16-235 integer coefficients used in RgbaToI420 on the C#
            // side). Without this, the H.264 bitstream carries no color metadata at all, and a
            // decoder is free to assume something else (e.g. full-range or BT.709) when decoding
            // for playback — that mismatch produces exactly the kind of persistent, uniform color
            // cast we've been chasing, and critically it would NOT be affected by swapping which
            // Image plane index gets the U vs V data (both R/B-channel-swap and U/V-plane-swap
            // fixes were ruled out via controlled tests before this was added).
            format.setInteger(MediaFormat.KEY_COLOR_RANGE, MediaFormat.COLOR_RANGE_LIMITED);
            format.setInteger(MediaFormat.KEY_COLOR_STANDARD, MediaFormat.COLOR_STANDARD_BT601_PAL);
            format.setInteger(MediaFormat.KEY_COLOR_TRANSFER, MediaFormat.COLOR_TRANSFER_SDR_VIDEO);

            encoder = MediaCodec.createEncoderByType(MIME_TYPE);
            encoder.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            encoder.start();

            muxer = new MediaMuxer(outPath, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4);

            pushFrameCallCount = 0;
            samplesWrittenCount = 0;

            return true;
        } catch (Exception e) {
            Log.e(TAG, "configure() failed for " + width + "x" + height + " -> " + outPath, e);
            release();
            return false;
        }
    }

    /**
     * Push one I420 frame: full-resolution Y plane, followed by half-resolution U plane, followed
     * by half-resolution V plane (all tightly packed, row stride == plane width). Width/height
     * must match what was passed to configure(). Returns false if the frame was dropped (e.g. no
     * input buffer was free in time) — caller may simply continue, a dropped frame just shows up
     * as a slightly shorter/choppier recording, it will not corrupt the file.
     */
    public synchronized boolean pushFrame(byte[] i420, long presentationTimeUs) {
        if (encoder == null) {
            Log.e(TAG, "pushFrame() called but encoder is not configured (call configure() first).");
            return false;
        }

        int expectedLength = frameWidth * frameHeight + 2 * ((frameWidth / 2) * (frameHeight / 2));
        if (i420 == null || i420.length != expectedLength) {
            Log.e(TAG, "pushFrame(): unexpected buffer length " + (i420 == null ? "null" : i420.length)
                + ", expected " + expectedLength + " for " + frameWidth + "x" + frameHeight + " I420.");
            return false;
        }

        pushFrameCallCount++;
        try {
            int inputIndex = encoder.dequeueInputBuffer(DEQUEUE_TIMEOUT_US);
            if (inputIndex < 0) {
                Log.w(TAG, "pushFrame(): no free input buffer, dropping frame at pts=" + presentationTimeUs
                    + " (call #" + pushFrameCallCount + ")");
                drainEncoder(false);
                return false;
            }

            Image image = encoder.getInputImage(inputIndex);
            if (image == null) {
                Log.e(TAG, "pushFrame(): getInputImage() returned null for index " + inputIndex + ".");
                encoder.queueInputBuffer(inputIndex, 0, 0, presentationTimeUs, 0);
                return false;
            }

            fillImageFromI420(image, i420, frameWidth, frameHeight, swapUAndVPlanes);

            int ySize = frameWidth * frameHeight;
            int uSize = (frameWidth / 2) * (frameHeight / 2);
            encoder.queueInputBuffer(inputIndex, 0, ySize + uSize * 2, presentationTimeUs, 0);

            drainEncoder(false);

            return true;
        } catch (Exception e) {
            Log.e(TAG, "pushFrame() failed at pts=" + presentationTimeUs + " (call #" + pushFrameCallCount + ")", e);
            return false;
        }
    }

    /**
     * Signals end-of-stream, drains remaining encoded frames into the muxer, finalizes the mp4,
     * and releases the codec/muxer. Returns the output path that was passed to configure() —
     * callers should still verify the file exists and is non-empty before treating this as a
     * successful recording (e.g. if zero frames were ever pushed, the file may be invalid).
     */
    public synchronized String stop() {
        try {
            if (encoder != null) {
                int inputIndex = encoder.dequeueInputBuffer(DEQUEUE_TIMEOUT_US_EOS);
                if (inputIndex >= 0) {
                    encoder.queueInputBuffer(inputIndex, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                } else {
                    Log.w(TAG, "stop(): no free input buffer to signal end-of-stream; draining anyway.");
                }
                drainEncoder(true);
            } else {
                Log.w(TAG, "stop(): encoder was already null — configure() may have failed earlier.");
            }
        } catch (Exception e) {
            Log.e(TAG, "stop() failed while draining", e);
        } finally {
            release();
        }
        return outputPath;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private void drainEncoder(boolean endOfStream) {
        if (encoder == null) return;
        while (true) {
            int outputIndex = encoder.dequeueOutputBuffer(bufferInfo, endOfStream ? DEQUEUE_TIMEOUT_US_EOS : 0);
            if (outputIndex == MediaCodec.INFO_TRY_AGAIN_LATER) {
                if (!endOfStream) return;
                // else keep looping until we actually see the EOS flag below
            } else if (outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                if (muxerStarted) {
                    Log.w(TAG, "drainEncoder(): output format changed again after muxer already started — ignoring.");
                    continue;
                }
                MediaFormat newFormat = encoder.getOutputFormat();
                trackIndex = muxer.addTrack(newFormat);
                muxer.start();
                muxerStarted = true;
            } else if (outputIndex >= 0) {
                ByteBuffer encodedData = encoder.getOutputBuffer(outputIndex);
                if (encodedData == null) {
                    Log.w(TAG, "drainEncoder(): getOutputBuffer(" + outputIndex + ") returned null, skipping.");
                    encoder.releaseOutputBuffer(outputIndex, false);
                    continue;
                }
                if ((bufferInfo.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
                    // Codec config data is already captured via addTrack()'s format; don't mux it as a sample.
                    bufferInfo.size = 0;
                }
                if (bufferInfo.size != 0) {
                    if (!muxerStarted) {
                        Log.w(TAG, "drainEncoder(): have encoded data but muxer not started yet — dropping sample.");
                    } else {
                        encodedData.position(bufferInfo.offset);
                        encodedData.limit(bufferInfo.offset + bufferInfo.size);
                        muxer.writeSampleData(trackIndex, encodedData, bufferInfo);
                        samplesWrittenCount++;
                    }
                }
                encoder.releaseOutputBuffer(outputIndex, false);
                if ((bufferInfo.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) {
                    return;
                }
            }
        }
    }

    private void release() {
        try {
            if (encoder != null) {
                encoder.stop();
                encoder.release();
            }
        } catch (Exception e) {
            Log.w(TAG, "release(): encoder cleanup error (safe to ignore if already stopped)", e);
        } finally {
            encoder = null;
        }
        try {
            if (muxer != null) {
                if (muxerStarted) {
                    muxer.stop();
                }
                muxer.release();
            }
        } catch (Exception e) {
            Log.w(TAG, "release(): muxer cleanup error", e);
        } finally {
            muxer = null;
            muxerStarted = false;
            trackIndex = -1;
        }
    }

    /**
     * Copies a tightly-packed I420 buffer (produced on the C# side) into whatever concrete plane
     * layout this device's encoder actually reports (COLOR_FormatYUV420Flexible covers both
     * planar (I420) and semi-planar (NV12) hardware, with device-specific row/pixel strides) —
     * using Image.getPlanes() rather than assuming a fixed layout is what makes this robust across
     * different phone chipsets/encoders.
     *
     * Android's documented convention is plane[0]=Y, plane[1]=U/Cb, plane[2]=V/Cr — that's true for
     * decoder/camera output, but some encoder implementations report getInputImage() planes with
     * U/V reversed from that for COLOR_FormatYUV420Flexible. A whole-frame yellow/orange color cast
     * (rather than a scrambled image) is the classic symptom of exactly that — swapUV corrects it.
     */
    private static void fillImageFromI420(Image image, byte[] i420, int width, int height, boolean swapUV) {
        Image.Plane[] planes = image.getPlanes();
        int ySize = width * height;
        int uvWidth = width / 2;
        int uvHeight = height / 2;
        int uSize = uvWidth * uvHeight;

        int uOffset = ySize;
        int vOffset = ySize + uSize;

        copyPlane(planes[0], i420, 0, width, height);
        copyPlane(planes[1], i420, swapUV ? vOffset : uOffset, uvWidth, uvHeight);
        copyPlane(planes[2], i420, swapUV ? uOffset : vOffset, uvWidth, uvHeight);
    }

    private static void copyPlane(Image.Plane plane, byte[] src, int srcOffset, int width, int height) {
        ByteBuffer buf = plane.getBuffer();
        int rowStride = plane.getRowStride();
        int pixelStride = plane.getPixelStride();
        int bufCapacity = buf.capacity();

        if (pixelStride == 1 && rowStride == width) {
            // Fully planar, no padding — every byte belongs to us, safe to bulk-copy.
            buf.clear();
            int toWrite = Math.min(width * height, bufCapacity);
            buf.put(src, srcOffset, toWrite);
            return;
        }

        if (pixelStride == 1) {
            // Planar but row-padded (rowStride > width) — still no interleaving with another
            // plane, so a per-row bulk copy at an absolute position is safe.
            for (int row = 0; row < height; row++) {
                int srcRowStart = srcOffset + row * width;
                int destRowStart = row * rowStride;
                int writeLen = Math.min(width, bufCapacity - destRowStart);
                if (writeLen <= 0) break;
                for (int i = 0; i < writeLen; i++) {
                    buf.put(destRowStart + i, src[srcRowStart + i]);
                }
            }
            return;
        }

        // pixelStride > 1: semi-planar interleaved chroma (e.g. NV12/NV21-style). This plane's
        // ByteBuffer is a VIEW into the SAME physical memory as the other chroma plane, just
        // offset by one byte — every byte we don't explicitly own (the interleaved neighbor's
        // samples) is still present in this buffer's addressable range. The previous
        // implementation built a full contiguous row buffer with zeros padded into those
        // not-owned byte slots and blitted the whole row in with a relative put() — since the
        // two chroma planes are written back-to-back, whichever one got written SECOND stomped
        // the first one's real values with its own zero padding, permanently zeroing out nearly
        // an entire chroma plane every frame (independent of which logical channel — U or V —
        // it held). Writing each owned sample individually via an ABSOLUTE put() never touches
        // the interleaved neighbor's bytes, so this is safe regardless of write order.
        for (int row = 0; row < height; row++) {
            int srcRowStart = srcOffset + row * width;
            int destRowStart = row * rowStride;
            for (int col = 0; col < width; col++) {
                int destPos = destRowStart + col * pixelStride;
                if (destPos >= bufCapacity) break;
                buf.put(destPos, src[srcRowStart + col]);
            }
        }
    }
}
