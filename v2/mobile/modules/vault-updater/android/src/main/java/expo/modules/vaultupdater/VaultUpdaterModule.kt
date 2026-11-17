package expo.modules.vaultupdater

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageInstaller
import android.net.Uri
import android.os.Build
import android.provider.Settings
import expo.modules.kotlin.Promise
import expo.modules.kotlin.exception.Exceptions
import expo.modules.kotlin.modules.Module
import expo.modules.kotlin.modules.ModuleDefinition
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

/**
 * Downloads a newer build of this app and installs it over itself through a
 * PackageInstaller session. On Android 12+ the session asks for no user
 * action, which Android grants to an app updating itself once the user has
 * allowed it to install apps. Where Android still wants a confirmation, its
 * own Update screen is shown and the outcome reported the same way.
 */
class VaultUpdaterModule : Module() {
  private val context: Context
    get() = appContext.reactContext ?: throw Exceptions.ReactContextLost()

  private var receiver: BroadcastReceiver? = null

  override fun definition() = ModuleDefinition {
    Name("VaultUpdater")
    Events("onProgress", "onStatus")

    Function("installedVersion") {
      val info = context.packageManager.getPackageInfo(context.packageName, 0)
      val code = if (Build.VERSION.SDK_INT >= 28) info.longVersionCode else @Suppress("DEPRECATION") info.versionCode.toLong()
      mapOf("versionCode" to code.toDouble(), "versionName" to (info.versionName ?: ""))
    }

    Function("canInstall") {
      Build.VERSION.SDK_INT < 26 || context.packageManager.canRequestPackageInstalls()
    }

    Function("openInstallSettings") {
      val intent = if (Build.VERSION.SDK_INT >= 26)
        Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + context.packageName))
      else Intent(Settings.ACTION_SECURITY_SETTINGS)
      intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
      context.startActivity(intent)
    }

    AsyncFunction("downloadAndInstall") { url: String, headers: Map<String, String>, sha256: String, promise: Promise ->
      // The download is long; keep it off the module's queue.
      Thread {
        try {
          val apk = download(url, headers)
          val actual = digest(apk)
          if (!actual.equals(sha256, ignoreCase = true)) {
            apk.delete()
            promise.reject("ERR_CHECKSUM", "The download was damaged. Please try again.", null)
            return@Thread
          }
          install(apk)
          promise.resolve(null)
        } catch (e: Exception) {
          promise.reject("ERR_UPDATE", e.message ?: "The update could not be installed.", e)
        }
      }.start()
    }

    OnDestroy { unregister() }
  }

  private fun download(url: String, headers: Map<String, String>): File {
    val dir = File(context.cacheDir, "update").apply { mkdirs() }
    dir.listFiles()?.forEach { it.delete() }
    val out = File(dir, "vault-movies.apk")
    val conn = URL(url).openConnection() as HttpURLConnection
    conn.connectTimeout = 15_000
    conn.readTimeout = 30_000
    headers.forEach { (k, v) -> conn.setRequestProperty(k, v) }
    try {
      if (conn.responseCode != 200) throw IllegalStateException("The server answered ${conn.responseCode}.")
      val total = conn.contentLengthLong
      var received = 0L
      var lastSent = 0L
      conn.inputStream.use { input ->
        out.outputStream().use { output ->
          val buf = ByteArray(64 * 1024)
          while (true) {
            val n = input.read(buf)
            if (n < 0) break
            output.write(buf, 0, n)
            received += n
            val now = System.currentTimeMillis()
            if (now - lastSent > 200) {
              lastSent = now
              sendEvent("onProgress", mapOf("received" to received.toDouble(), "total" to total.toDouble()))
            }
          }
        }
      }
      sendEvent("onProgress", mapOf("received" to received.toDouble(), "total" to received.toDouble()))
    } finally {
      conn.disconnect()
    }
    return out
  }

  private fun digest(f: File): String {
    val md = MessageDigest.getInstance("SHA-256")
    f.inputStream().use { input ->
      val buf = ByteArray(64 * 1024)
      while (true) {
        val n = input.read(buf)
        if (n < 0) break
        md.update(buf, 0, n)
      }
    }
    return md.digest().joinToString("") { "%02x".format(it) }
  }

  private fun install(apk: File) {
    val installer = context.packageManager.packageInstaller
    val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
    params.setAppPackageName(context.packageName)
    params.setSize(apk.length())
    if (Build.VERSION.SDK_INT >= 31) {
      params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
    }
    val sessionId = installer.createSession(params)
    installer.openSession(sessionId).use { session ->
      session.openWrite("base.apk", 0, apk.length()).use { out ->
        apk.inputStream().use { it.copyTo(out, 64 * 1024) }
        session.fsync(out)
      }
      register()
      val action = context.packageName + ".UPDATE_STATUS"
      val intent = Intent(action).setPackage(context.packageName)
      val flags = PendingIntent.FLAG_UPDATE_CURRENT or
        (if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0)
      val pending = PendingIntent.getBroadcast(context, sessionId, intent, flags)
      session.commit(pending.intentSender)
    }
  }

  private fun register() {
    if (receiver != null) return
    val r = object : BroadcastReceiver() {
      override fun onReceive(ctx: Context, intent: Intent) {
        when (val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
          PackageInstaller.STATUS_PENDING_USER_ACTION -> {
            @Suppress("DEPRECATION")
            val confirm = if (Build.VERSION.SDK_INT >= 33)
              intent.getParcelableExtra(Intent.EXTRA_INTENT, Intent::class.java)
            else intent.getParcelableExtra(Intent.EXTRA_INTENT)
            if (confirm != null) {
              confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
              (appContext.currentActivity ?: ctx).startActivity(confirm)
              sendEvent("onStatus", mapOf("status" to "confirm"))
            } else {
              sendEvent("onStatus", mapOf("status" to "failed", "message" to "Android did not offer the update."))
            }
          }
          PackageInstaller.STATUS_SUCCESS -> sendEvent("onStatus", mapOf("status" to "success"))
          else -> {
            val msg = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
            val text = when (status) {
              PackageInstaller.STATUS_FAILURE_ABORTED -> "The update was cancelled."
              PackageInstaller.STATUS_FAILURE_CONFLICT,
              PackageInstaller.STATUS_FAILURE_INCOMPATIBLE -> "This update does not match the installed app."
              PackageInstaller.STATUS_FAILURE_STORAGE -> "There is not enough space on the phone."
              else -> msg ?: "The update could not be installed."
            }
            sendEvent("onStatus", mapOf("status" to "failed", "message" to text))
          }
        }
      }
    }
    val filter = IntentFilter(context.packageName + ".UPDATE_STATUS")
    if (Build.VERSION.SDK_INT >= 33) context.registerReceiver(r, filter, Context.RECEIVER_NOT_EXPORTED)
    else context.registerReceiver(r, filter)
    receiver = r
  }

  private fun unregister() {
    receiver?.let { try { context.unregisterReceiver(it) } catch (_: Exception) {} }
    receiver = null
  }
}
