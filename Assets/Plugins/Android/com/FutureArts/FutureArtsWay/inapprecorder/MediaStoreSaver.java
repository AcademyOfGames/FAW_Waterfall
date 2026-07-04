package com.FutureArts.FutureArtsWay.inapprecorder;

import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Environment;
import android.provider.MediaStore;
import android.util.Log;

import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.OutputStream;

/**
 * Copies a finished mp4 (written to app-private storage by InAppFrameEncoder) into the public
 * Movies gallery via MediaStore, and hands a saved clip off to the native OS share sheet.
 *
 * Uses the scoped-storage MediaStore API — this project's AndroidMinSdkVersion is 35, so every
 * device this ships to is well past the Android 10 (API 29) scoped-storage cutover, meaning no
 * WRITE_EXTERNAL_STORAGE runtime permission is needed for the app to insert its own media.
 */
public class MediaStoreSaver {
    private static final String TAG = "MediaStoreSaver";

    /** Returns the resulting content:// URI as a String, or null on failure. */
    public String saveMp4ToMovies(Context context, String srcPath, String displayName) {
        ContentResolver resolver = context.getContentResolver();
        ContentValues values = new ContentValues();
        values.put(MediaStore.Video.Media.DISPLAY_NAME, displayName);
        values.put(MediaStore.Video.Media.MIME_TYPE, "video/mp4");
        values.put(MediaStore.Video.Media.RELATIVE_PATH, Environment.DIRECTORY_MOVIES + "/FutureArtsWay");
        values.put(MediaStore.Video.Media.IS_PENDING, 1);

        Uri itemUri = null;
        try {
            itemUri = resolver.insert(MediaStore.Video.Media.EXTERNAL_CONTENT_URI, values);
            if (itemUri == null) {
                Log.e(TAG, "saveMp4ToMovies(): resolver.insert() returned null.");
                return null;
            }

            long bytesCopied = 0;
            try (OutputStream out = resolver.openOutputStream(itemUri);
                 FileInputStream in = new FileInputStream(new File(srcPath))) {
                if (out == null) {
                    Log.e(TAG, "saveMp4ToMovies(): openOutputStream() returned null for " + itemUri);
                    return null;
                }
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = in.read(buffer)) != -1) {
                    out.write(buffer, 0, read);
                    bytesCopied += read;
                }
            }

            values.clear();
            values.put(MediaStore.Video.Media.IS_PENDING, 0);
            resolver.update(itemUri, values, null, null);

            return itemUri.toString();
        } catch (IOException e) {
            Log.e(TAG, "saveMp4ToMovies() failed for " + srcPath, e);
            if (itemUri != null) {
                try {
                    resolver.delete(itemUri, null, null);
                } catch (Exception ignored) {
                    // best-effort cleanup of the half-written MediaStore entry
                }
            }
            return null;
        }
    }

    /** Opens the native OS share sheet (chooser) for an already-saved content:// URI. */
    public boolean shareContentUri(Context context, String uriString, String mimeType, String chooserTitle) {
        try {
            Uri uri = Uri.parse(uriString);
            Intent sendIntent = new Intent(Intent.ACTION_SEND);
            sendIntent.setType(mimeType);
            sendIntent.putExtra(Intent.EXTRA_STREAM, uri);
            sendIntent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);

            Intent chooser = Intent.createChooser(sendIntent, chooserTitle);
            chooser.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            context.startActivity(chooser);
            return true;
        } catch (Exception e) {
            Log.e(TAG, "shareContentUri() failed for " + uriString, e);
            return false;
        }
    }
}
