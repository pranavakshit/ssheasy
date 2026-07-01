package com.pranavakshit.ssh.data

data class ConnectionProfile(
    val id: String = java.util.UUID.randomUUID().toString(),
    val name: String,
    val host: String,
    val port: Int = 22,
    val username: String,
    val keyFilePath: String,
    val passphrase: String? = null,
    val lastConnected: Long = 0
)
