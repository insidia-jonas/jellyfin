package org.jellyfin.firetv.core

/**
 * Injects the NativeShell bootstrap into the hosted jellyfin-web HTML so the TV layout
 * and native bridge exist before application bundles run.
 */
object NativeShellInjector {
    fun inject(html: String, scriptSrc: String = FireTvClient.NATIVE_SHELL_SCRIPT): String {
        if (html.contains(scriptSrc)) {
            return html
        }
        val tag = """<script src="$scriptSrc" charset="utf-8"></script>"""
        val head = Regex("<head(\\s[^>]*)?>", RegexOption.IGNORE_CASE).find(html)
        return if (head != null) {
            html.replaceRange(head.range.last + 1, head.range.last + 1, tag)
        } else {
            tag + html
        }
    }
}
