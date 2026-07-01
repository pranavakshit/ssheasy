package com.pranavakshit.ssh.services

import android.content.Context
import com.pranavakshit.ssh.data.ConnectionProfile
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import net.schmizz.sshj.SSHClient
import net.schmizz.sshj.connection.channel.direct.Session
import net.schmizz.sshj.sftp.SFTPClient
import net.schmizz.sshj.sftp.RemoteResourceInfo
import net.schmizz.sshj.transport.verification.PromiscuousVerifier
import java.io.File
import java.io.InputStream
import java.io.OutputStream
import java.security.Security
import org.bouncycastle.jce.provider.BouncyCastleProvider

class SshService {

    init {
        Security.removeProvider("BC")
        Security.insertProviderAt(BouncyCastleProvider(), 1)
    }

    private var sshClient: SSHClient? = null
    private var sftpClient: SFTPClient? = null
    private var shellSession: Session? = null
    private var shellChannel: Session.Shell? = null

    val isConnected: Boolean
        get() = sshClient?.isConnected == true && sshClient?.isAuthenticated == true

    suspend fun connect(profile: ConnectionProfile, context: Context, onDataReceived: (ByteArray) -> Unit) = withContext(Dispatchers.IO) {
        sshClient = SSHClient()
        sshClient?.addHostKeyVerifier(PromiscuousVerifier())
        sshClient?.connect(profile.host, profile.port)

        // Read the copied private key file from internal storage
        val keyFile = File(profile.keyFilePath)
        if (!keyFile.exists()) {
            throw Exception("Key file not found at ${profile.keyFilePath}")
        }

        val keyProvider = if (profile.passphrase.isNullOrEmpty()) {
            sshClient?.loadKeys(keyFile.absolutePath)
        } else {
            sshClient?.loadKeys(keyFile.absolutePath, profile.passphrase.toCharArray())
        }
        sshClient?.authPublickey(profile.username, keyProvider)

        sftpClient = sshClient?.newSFTPClient()
    }

    suspend fun startTerminal(): Pair<InputStream, OutputStream>? = withContext(Dispatchers.IO) {
        if (!isConnected) return@withContext null
        try {
            shellSession = sshClient?.startSession()
            shellSession?.allocateDefaultPTY()
            shellChannel = shellSession?.startShell()
            val input = shellChannel?.inputStream
            val output = shellChannel?.outputStream
            if (input != null && output != null) {
                return@withContext input to output
            } else {
                return@withContext null
            }
        } catch (e: Exception) {
            e.printStackTrace()
            return@withContext null
        }
    }

    suspend fun listFiles(path: String = "."): List<RemoteResourceInfo> = withContext(Dispatchers.IO) {
        if (!isConnected) throw Exception("Not connected")
        return@withContext sftpClient?.ls(path) ?: emptyList()
    }

    suspend fun deleteFile(path: String) = withContext(Dispatchers.IO) {
        if (!isConnected) throw Exception("Not connected")
        val stat = sftpClient?.stat(path)
        if (stat?.type == net.schmizz.sshj.sftp.FileMode.Type.DIRECTORY) {
            sftpClient?.rmdir(path)
        } else {
            sftpClient?.rm(path)
        }
    }

    suspend fun uploadFile(localPath: String, remotePath: String) = withContext(Dispatchers.IO) {
        if (!isConnected) throw Exception("Not connected")
        sftpClient?.put(localPath, remotePath)
    }

    fun disconnect() {
        try { shellChannel?.close() } catch (e: Exception) {}
        try { shellSession?.close() } catch (e: Exception) {}
        try { sftpClient?.close() } catch (e: Exception) {}
        try { sshClient?.disconnect() } catch (e: Exception) {}
        shellChannel = null
        shellSession = null
        sftpClient = null
        sshClient = null
    }
}
