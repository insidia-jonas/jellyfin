# Keep JavascriptInterface entry points if release shrinking is enabled later.
-keepclassmembers class org.jellyfin.firetv.shell.NativeInterface {
    @android.webkit.JavascriptInterface <methods>;
}
