package com.pranavakshit.ssh.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.pranavakshit.ssh.data.ConnectionProfile
import com.pranavakshit.ssh.data.ProfileManager
import com.pranavakshit.ssh.services.SshService
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import net.schmizz.sshj.sftp.RemoteResourceInfo
import java.io.InputStream
import java.io.OutputStream

class MainViewModel(application: Application) : AndroidViewModel(application) {
    private val profileManager = ProfileManager(application)
    val profiles = profileManager.profilesFlow

    val sshService = SshService()

    private val _isConnected = MutableStateFlow(false)
    val isConnected = _isConnected.asStateFlow()

    private val _isConnecting = MutableStateFlow(false)
    val isConnecting = _isConnecting.asStateFlow()

    private val _connectionError = MutableStateFlow<String?>(null)
    val connectionError = _connectionError.asStateFlow()

    private val _currentPath = MutableStateFlow(".")
    val currentPath = _currentPath.asStateFlow()

    private val _files = MutableStateFlow<List<RemoteResourceInfo>>(emptyList())
    val files = _files.asStateFlow()

    private val _terminalOutput = MutableStateFlow("")
    val terminalOutput = _terminalOutput.asStateFlow()

    private var terminalOutputStream: OutputStream? = null

    fun addProfile(name: String, host: String, username: String, keyPath: String, passphrase: String?) {
        profileManager.addProfile(
            ConnectionProfile(name = name, host = host, username = username, keyFilePath = keyPath, passphrase = passphrase)
        )
    }

    fun deleteProfile(id: String) {
        profileManager.deleteProfile(id)
    }

    fun connect(profile: ConnectionProfile, context: android.content.Context) {
        viewModelScope.launch {
            _connectionError.value = null
            _isConnecting.value = true
            try {
                sshService.connect(profile, context) { }
                _isConnected.value = true
                loadFiles(".")
                startTerminalSession()
            } catch (e: Exception) {
                e.printStackTrace()
                _isConnected.value = false
                _connectionError.value = e.message ?: "Unknown connection error"
            } finally {
                _isConnecting.value = false
            }
        }
    }

    fun clearError() {
        _connectionError.value = null
    }

    private fun startTerminalSession() {
        viewModelScope.launch {
            val streams = sshService.startTerminal()
            if (streams != null) {
                val (inputStream, outputStream) = streams
                terminalOutputStream = outputStream
                readTerminalOutput(inputStream)
            }
        }
    }

    private fun readTerminalOutput(inputStream: InputStream) {
        viewModelScope.launch(kotlinx.coroutines.Dispatchers.IO) {
            val ansiRegex = Regex("\u001B\\[[;\\d]*[ -/]*[@-~]")
            try {
                val buffer = ByteArray(1024)
                var bytesRead: Int
                while (inputStream.read(buffer).also { bytesRead = it } != -1) {
                    val text = String(buffer, 0, bytesRead)
                    val cleanText = text.replace(ansiRegex, "")
                    
                    // limit terminal buffer size to avoid OOM
                    val currentText = _terminalOutput.value + cleanText
                    _terminalOutput.value = if (currentText.length > 50000) currentText.takeLast(50000) else currentText
                }
            } catch (e: Exception) {
                e.printStackTrace()
            }
        }
    }

    fun sendTerminalCommand(command: String) {
        viewModelScope.launch(kotlinx.coroutines.Dispatchers.IO) {
            try {
                terminalOutputStream?.write((command + "\n").toByteArray())
                terminalOutputStream?.flush()
            } catch (e: Exception) {
                e.printStackTrace()
            }
        }
    }

    fun loadFiles(path: String) {
        viewModelScope.launch {
            try {
                _currentPath.value = path
                val fetchedFiles = sshService.listFiles(path)
                _files.value = fetchedFiles.sortedBy { if (it.isDirectory) 0 else 1 }
            } catch (e: Exception) {
                e.printStackTrace()
            }
        }
    }

    fun deleteFile(path: String) {
        viewModelScope.launch {
            try {
                sshService.deleteFile(path)
                loadFiles(_currentPath.value) // refresh
            } catch (e: Exception) {
                _connectionError.value = "Failed to delete: ${e.message}"
            }
        }
    }

    fun disconnect() {
        sshService.disconnect()
        _isConnected.value = false
        _files.value = emptyList()
        _terminalOutput.value = ""
        terminalOutputStream = null
    }
}
