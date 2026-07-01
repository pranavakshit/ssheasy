package com.pranavakshit.ssh.data

import android.content.Context
import com.google.gson.Gson
import com.google.gson.reflect.TypeToken
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

class ProfileManager(context: Context) {
    private val prefs = context.getSharedPreferences("ssh_profiles", Context.MODE_PRIVATE)
    private val gson = Gson()
    
    private val _profilesFlow = MutableStateFlow<List<ConnectionProfile>>(emptyList())
    val profilesFlow = _profilesFlow.asStateFlow()

    init {
        loadProfiles()
    }

    private fun loadProfiles() {
        val json = prefs.getString("profiles", "[]")
        val type = object : TypeToken<List<ConnectionProfile>>() {}.type
        val list: List<ConnectionProfile> = gson.fromJson(json, type) ?: emptyList()
        _profilesFlow.value = list.sortedByDescending { it.lastConnected }
    }

    fun addProfile(profile: ConnectionProfile) {
        val list = _profilesFlow.value.toMutableList()
        list.add(profile)
        saveProfiles(list)
    }

    fun updateProfile(profile: ConnectionProfile) {
        val list = _profilesFlow.value.map { if (it.id == profile.id) profile else it }
        saveProfiles(list)
    }

    fun deleteProfile(id: String) {
        val list = _profilesFlow.value.filter { it.id != id }
        saveProfiles(list)
    }

    private fun saveProfiles(list: List<ConnectionProfile>) {
        val sorted = list.sortedByDescending { it.lastConnected }
        prefs.edit().putString("profiles", gson.toJson(sorted)).apply()
        _profilesFlow.value = sorted
    }
}
