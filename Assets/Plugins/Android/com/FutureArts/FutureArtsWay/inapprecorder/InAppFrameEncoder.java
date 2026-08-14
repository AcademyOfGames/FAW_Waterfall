package com.FutureArts.FutureArtsWay.inapprecorder;

import android.media.Image;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.MediaMuxer;
import android.util.Log;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;

/**
 * Minimal on-device H.264 encoder that takes raw I420 (planar YUV420) frames pushed from Unity,
 * optionally an AAC audio track built from raw PCM16 samples pushed from Unity's own audio mixer,
 * and muxes them into an mp4 file.
 *
 * Deliberately does NOT use Android's MediaProjection screen-capture broker (video) or
 * AudioPlaybackCaptureConfiguration (audio) — both require a live MediaProjection consent token,
 * i.e. exactly the "Share your screen with FutureArtsWay?" system dialog this whole custom recorder
 * exists to avoid. Frames come from Unity's own already-rendered pixels (ScreenCapture side, see
 * InAppScreenRecorder.cs) and audio comes from Unity's own audio-thread mixer tap (see
 * AudioCaptureTap.cs) — both are just the app handing us data it already produced itself.
 *
 * Audio is optional: pass audioSampleRate <= 0 to configure() to record video-only (e.g. if no
 * AudioListener was found in the scene). When enabled, MediaMuxer requires every track to be
 * added via addTrack() before muxer.start() is called even once — tryStartMuxer() gates that until
 * both the video and (if enabled) audio encoders have each reported their INFO_OUTPUT_FORMAT_CHANGED.
 *
 * Threading: configure() and stop() are called from Unity's main thread; pushFrame() and
 * pushAudioSamples() are called from InAppScreenRecorder's dedicated encode thread (a JVM-attached
 * background thread), never from the main thread — queueing frames into MediaCodec and draining its
 * output are both expensive enough to visibly cost the app framerate if done inline with rendering.
 * The recorder guarantees that thread has exited before stop() runs, so the synchronized on every
 * public method is what makes the hand-off between the two threads safe.
 */
public class InAppFrameEncoder {
    private static final String TAG = "InAppFrameEncoder";
    private static final String VIDEO_MIME_TYPE = "video/avc";
    private static final String AUDIO_MIME_TYPE = "audio/mp4a-latm";
    private static final int I_FRAME_INTERVAL_SECONDS = 2;
    private static final int AUDIO_BIT_RATE = 128_000;
    private static final long DEQUEUE_TIMEOUT_US = 10_000; // 10ms
    private static final long DEQUEUE_TIMEOUT_US_EOS = 250_000; // 250ms, only used while draining at stop()
    private static final int MAX_INPUT_BUFFER_WAIT_RETRIES = 5;

    private MediaCodec videoEncoder;
    private MediaCodec audioEncoder; // null when recording video-only
    private MediaMuxer muxer;
    private MediaCodec.BufferInfo videoBufferInfo;
    private MediaCodec.BufferInfo audioBufferInfo;
    private int videoTrackIndex = -1;
    private int audioTrackIndex = -1;
    private boolean audioEnabled = false;
    private boolean muxerStarted = false;
    private int frameWidth;
    private int frameHeight;
    private String outputPath;
    private long pushFrameCallCount = 0;
    private long pushAudioCallCount = 0;
    private long samplesWrittenCount = 0;
    private boolean swapUAndVPlanes = true;
    /** Reused staging buffer for pushAudioSamples(). Direct (not heap) so the copy into
     * MediaCodec's own direct input buffer is a straight memcpy, and reused across calls because
     * pushAudioSamples() runs once per rendered frame — allocating a fresh ByteBuffer each time
     * was pure garbage-collector churn. Grown on demand, never shrunk. */
    private ByteBuffer pcmScratch;

    /** Returns true on success. Logs and returns false (with cleanup already done) on failure.
     * Pass audioSampleRate <= 0 to record video-only (no audio track at all). */
    public synchronized boolean configure(String outPath, int width, int height, int frameRate, int bitRate,
            boolean swapUV, int audioSampleRate, int audioChannelCount) {
        try {
            outputPath = outPath;
            frameWidth = width;
            frameHeight = height;
            swapUAndVPlanes = swapUV;
            videoBufferInfo = new MediaCodec.BufferInfo();
            audioBufferInfo = new MediaCodec.BufferInfo();
            videoTrackIndex = -1;
            audioTrackIndex = -1;
            muxerStarted = false;
            audioEnabled = audioSampleRate > 0 && audioChannelCount > 0;

            MediaFormat format = MediaFormat.createVideoFormat(VIDEO_MIME_TYPE, width, height);
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

            videoEncoder = MediaCodec.createEncoderByType(VIDEO_MIME_TYPE);
            videoEncoder.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            videoEncoder.start();

            if (audioEnabled) {
                MediaFormat audioFormat = MediaFormat.createAudioFormat(AUDIO_MIME_TYPE, audioSampleRate, audioChannelCount);
                audioFormat.setInteger(MediaFormat.KEY_AAC_PROFILE, MediaCodecInfo.CodecProfileLevel.AACObjectLC);
                audioFormat.setInteger(MediaFormat.KEY_BIT_RATE, AUDIO_BIT_RATE);
                audioEncoder = MediaCodec.createEncoderByType(AUDIO_MIME_TYPE);
                audioEncoder.configure(audioFormat, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
                audioEncoder.start();
            } else {
                audioEncoder = null;
            }

            muxer = new MediaMuxer(outPath, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4);

            pushFrameCallCount = 0;
            pushAudioCallCount = 0;
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
        if (videoEncoder == null) {
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
            int inputIndex = videoEncoder.dequeueInputBuffer(DEQUEUE_TIMEOUT_US);
            if (inputIndex < 0) {
                Log.w(TAG, "pushFrame(): no free input buffer, dropping frame at pts=" + presentationTimeUs
                    + " (call #" + pushFrameCallCount + ")");
                drainVideoEncoder(false);
                return false;
            }

            Image image = videoEncoder.getInputImage(inputIndex);
            if (image == null) {
                Log.e(TAG, "pushFrame(): getInputImage() returned null for index " + inputIndex + ".");
                videoEncoder.queueInputBuffer(inputIndex, 0, 0, presentationTimeUs, 0);
                return false;
            }

            fillImageFromI420(image, i420, frameWidth, frameHeight, swapUAndVPlanes);

            int ySize = frameWidth * frameHeight;
            int uSize = (frameWidth / 2) * (frameHeight / 2);
            videoEncoder.queueInputBuffer(inputIndex, 0, ySize + uSize * 2, presentationTimeUs, 0);

            drainVideoEncoder(false);

            return true;
        } catch (Exception e) {
            Log.e(TAG, "pushFrame() failed at pts=" + presentationTimeUs + " (call #" + pushFrameCallCount + ")", e);
            return false;
        }
    }

    /**
     * Push a chunk of interleaved 16-bit PCM audio samples (e.g. [L,R,L,R,...] for stereo) captured
     * from Unity's own audio mixer (see AudioCaptureTap.cs) — NOT from the microphone or from
     * Android's AudioPlaybackCaptureConfiguration. No-ops (returns false) if configure() was called
     * with audioSampleRate <= 0, i.e. audio was not requested for this recording. Loops internally
     * since a chunk from Unity may be larger than one MediaCodec input buffer; drops only the
     * un-queued remainder (with a warning) if the encoder can't keep up, same "drop rather than
     * stall" philosophy as pushFrame().
     */
    public synchronized boolean pushAudioSamples(short[] pcm, long presentationTimeUs) {
        if (!audioEnabled || audioEncoder == null) {
            return false;
        }
        if (pcm == null || pcm.length == 0) {
            return true; // nothing to do, not an error
        }

        pushAudioCallCount++;
        try {
            int neededBytes = pcm.length * 2;
            if (pcmScratch == null || pcmScratch.capacity() < neededBytes) {
                pcmScratch = ByteBuffer.allocateDirect(Math.max(neededBytes, 16384)).order(ByteOrder.LITTLE_ENDIAN);
            }
            ByteBuffer pcmBytes = pcmScratch;
            pcmBytes.clear();
            pcmBytes.limit(neededBytes);
            // Bulk short[] -> little-endian bytes in one shot, instead of a putShort() per sample.
            pcmBytes.asShortBuffer().put(pcm);

            int totalBytes = neededBytes;
            int offset = 0;
            while (offset < totalBytes) {
                int inputIndex = -1;
                int retries = 0;
                while (retries < MAX_INPUT_BUFFER_WAIT_RETRIES) {
                    inputIndex = audioEncoder.dequeueInputBuffer(DEQUEUE_TIMEOUT_US);
                    if (inputIndex >= 0) break;
                    drainAudioEncoder(false);
                    retries++;
                }
                if (inputIndex < 0) {
                    Log.w(TAG, "pushAudioSamples(): no free input buffer after " + MAX_INPUT_BUFFER_WAIT_RETRIES +
                        " retries, dropping remaining " + (totalBytes - offset) + " bytes at pts=" +
                        presentationTimeUs + " (call #" + pushAudioCallCount + ")");
                    return false;
                }

                ByteBuffer inputBuffer = audioEncoder.getInputBuffer(inputIndex);
                if (inputBuffer == null) {
                    Log.e(TAG, "pushAudioSamples(): getInputBuffer() returned null for index " + inputIndex + ".");
                    audioEncoder.queueInputBuffer(inputIndex, 0, 0, presentationTimeUs, 0);
                    continue;
                }
                inputBuffer.clear();
                int chunk = Math.min(inputBuffer.capacity(), totalBytes - offset);
                pcmBytes.limit(offset + chunk);
                pcmBytes.position(offset);
                inputBuffer.put(pcmBytes);

                audioEncoder.queueInputBuffer(inputIndex, 0, chunk, presentationTimeUs, 0);

                offset += chunk;
                drainAudioEncoder(false);
            }
            return true;
        } catch (Exception e) {
            Log.e(TAG, "pushAudioSamples() failed at pts=" + presentationTimeUs + " (call #" + pushAudioCallCount + ")", e);
            return false;
        }
    }

    /**
     * Signals end-of-stream on whichever encoders are active, drains remaining encoded frames into
     * the muxer, finalizes the mp4, and releases the codec(s)/muxer. Returns the output path that
     * was passed to configure() — callers should still verify the file exists and is non-empty
     * before treating this as a successful recording (e.g. if zero frames were ever pushed, the
     * file may be invalid).
     */
    public synchronized String stop() {
        try {
            if (videoEncoder != null) {
                int inputIndex = videoEncoder.dequeueInputBuffer(DEQUEUE_TIMEOUT_US_EOS);
                if (inputIndex >= 0) {
                    videoEncoder.queueInputBuffer(inputIndex, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                } else {
                    Log.w(TAG, "stop(): no free video input buffer to signal end-of-stream; draining anyway.");
                }
                drainVideoEncoder(true);
            } else {
                Log.w(TAG, "stop(): video encoder was already null — configure() may have failed earlier.");
            }

            if (audioEnabled && audioEncoder != null) {
                int inputIndex = audioEncoder.dequeueInputBuffer(DEQUEUE_TIMEOUT_US_EOS);
                if (inputIndex >= 0) {
                    audioEncoder.queueInputBuffer(inputIndex, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                } else {
                    Log.w(TAG, "stop(): no free audio input buffer to signal end-of-stream; draining anyway.");
                }
                drainAudioEncoder(true);
            }
        } catch (Exception e) {
            Log.e(TAG, "stop() failed while draining", e);
        } finally {
            release();
        }
        return outputPath;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private void tryStartMuxer() {
        if (muxerStarted) return;
        boolean videoReady = videoTrackIndex >= 0;
        boolean audioReady = !audioEnabled || audioTrackIndex >= 0;
        if (videoReady && audioReady) {
            muxer.start();
            muxerStarted = true;
        }
    }

    private void drainVideoEncoder(boolean endOfStream) {
        if (videoEncoder == null) return;
        while (true) {
            int outputIndex = videoEncoder.dequeueOutputBuffer(videoBufferInfo, endOfStream ? DEQUEUE_TIMEOUT_US_EOS : 0);
            if (outputIndex == MediaCodec.INFO_TRY_AGAIN_LATER) {
                if (!endOfStream) return;
                // else keep looping until we actually see the EOS flag below
            } else if (outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                if (videoTrackIndex >= 0) {
                    Log.w(TAG, "drainVideoEncoder(): output format changed again after track already added — ignoring.");
                    continue;
                }
                MediaFormat newFormat = videoEncoder.getOutputFormat();
                videoTrackIndex = muxer.addTrack(newFormat);
                tryStartMuxer();
            } else if (outputIndex >= 0) {
                ByteBuffer encodedData = videoEncoder.getOutputBuffer(outputIndex);
                if (encodedData == null) {
                    Log.w(TAG, "drainVideoEncoder(): getOutputBuffer(" + outputIndex + ") returned null, skipping.");
                    videoEncoder.releaseOutputBuffer(outputIndex, false);
                    continue;
                }
                if ((videoBufferInfo.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
                    // Codec config data is already captured via addTrack()'s format; don't mux it as a sample.
                    videoBufferInfo.size = 0;
                }
                if (videoBufferInfo.size != 0) {
                    if (!muxerStarted) {
                        Log.w(TAG, "drainVideoEncoder(): have encoded data but muxer not started yet — dropping sample.");
                    } else {
                        encodedData.position(videoBufferInfo.offset);
                        encodedData.limit(videoBufferInfo.offset + videoBufferInfo.size);
                        muxer.writeSampleData(videoTrackIndex, encodedData, videoBufferInfo);
                        samplesWrittenCount++;
                    }
                }
                videoEncoder.releaseOutputBuffer(outputIndex, false);
                if ((videoBufferInfo.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) {
                    return;
                }
            }
        }
    }

    private void drainAudioEncoder(boolean endOfStream) {
        if (audioEncoder == null) return;
        while (true) {
            int outputIndex = audioEncoder.dequeueOutputBuffer(audioBufferInfo, endOfStream ? DEQUEUE_TIMEOUT_US_EOS : 0);
            if (outputIndex == MediaCodec.INFO_TRY_AGAIN_LATER) {
                if (!endOfStream) return;
                // else keep looping until we actually see the EOS flag below
            } else if (outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                if (audioTrackIndex >= 0) {
                    Log.w(TAG, "drainAudioEncoder(): output format changed again after track already added — ignoring.");
                    continue;
                }
                MediaFormat newFormat = audioEncoder.getOutputFormat();
                audioTrackIndex = muxer.addTrack(newFormat);
                tryStartMuxer();
            } else if (outputIndex >= 0) {
                ByteBuffer encodedData = audioEncoder.getOutputBuffer(outputIndex);
                if (encodedData == null) {
                    Log.w(TAG, "drainAudioEncoder(): getOutputBuffer(" + outputIndex + ") returned null, skipping.");
                    audioEncoder.releaseOutputBuffer(outputIndex, false);
                    continue;
                }
                if ((audioBufferInfo.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
                    audioBufferInfo.size = 0;
                }
                if (audioBufferInfo.size != 0) {
                    if (!muxerStarted) {
                        Log.w(TAG, "drainAudioEncoder(): have encoded data but muxer not started yet — dropping sample.");
                    } else {
                        encodedData.position(audioBufferInfo.offset);
                        encodedData.limit(audioBufferInfo.offset + audioBufferInfo.size);
                        muxer.writeSampleData(audioTrackIndex, encodedData, audioBufferInfo);
                    }
                }
                audioEncoder.releaseOutputBuffer(outputIndex, false);
                if ((audioBufferInfo.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) {
                    return;
                }
            }
        }
    }

    private void release() {
        try {
            if (videoEncoder != null) {
                videoEncoder.stop();
                videoEncoder.release();
            }
        } catch (Exception e) {
            Log.w(TAG, "release(): video encoder cleanup error (safe to ignore if already stopped)", e);
        } finally {
            videoEncoder = null;
        }
        try {
            if (audioEncoder != null) {
                audioEncoder.stop();
                audioEncoder.release();
            }
        } catch (Exception e) {
            Log.w(TAG, "release(): audio encoder cleanup error (safe to ignore if already stopped)", e);
        } finally {
            audioEncoder = null;
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
            videoTrackIndex = -1;
            audioTrackIndex = -1;
            pcmScratch = null;
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
