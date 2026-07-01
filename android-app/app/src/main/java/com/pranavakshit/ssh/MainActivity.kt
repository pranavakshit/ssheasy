package com.pranavakshit.ssh

import android.content.Context
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Clear
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.InsertDriveFile
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.navigation.compose.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import com.pranavakshit.ssh.data.ConnectionProfile
import com.pranavakshit.ssh.ui.MainViewModel
import java.io.File
import java.io.FileOutputStream

class MainActivity : ComponentActivity() {
    private val viewModel: MainViewModel by viewModels()

    private var pendingKeySelection: ((String) -> Unit)? = null

    private val pickKeyFile = registerForActivityResult(ActivityResultContracts.GetContent()) { uri: Uri? ->
        uri?.let {
            val keyPath = copyFileToInternalStorage(it, this)
            pendingKeySelection?.invoke(keyPath)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            MaterialTheme {
                Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
                    AppNavigation(viewModel, onPickKey = { callback ->
                        pendingKeySelection = callback
                        pickKeyFile.launch("*/*")
                    })
                }
            }
        }
    }

    private fun copyFileToInternalStorage(uri: Uri, context: Context): String {
        val keysDir = File(context.filesDir, "keys")
        if (!keysDir.exists()) keysDir.mkdirs()
        val destFile = File(keysDir, "key_${System.currentTimeMillis()}")
        
        context.contentResolver.openInputStream(uri)?.use { input ->
            FileOutputStream(destFile).use { output ->
                input.copyTo(output)
            }
        }
        return destFile.absolutePath
    }
}

@Composable
fun AppNavigation(viewModel: MainViewModel, onPickKey: ((String) -> Unit) -> Unit) {
    val navController = rememberNavController()
    val isConnected by viewModel.isConnected.collectAsState()

    NavHost(navController = navController, startDestination = if (isConnected) "connected" else "profiles") {
        composable("profiles") {
            ProfilesScreen(
                viewModel = viewModel,
                onAddProfile = { navController.navigate("add_profile") },
                onConnect = { profile ->
                    viewModel.connect(profile, navController.context)
                    navController.navigate("connected") {
                        popUpTo("profiles") { inclusive = true }
                    }
                }
            )
        }
        composable("add_profile") {
            AddProfileScreen(
                viewModel = viewModel,
                onPickKey = onPickKey,
                onBack = { navController.popBackStack() }
            )
        }
        composable("connected") {
            ConnectedScreen(
                viewModel = viewModel,
                onDisconnect = {
                    viewModel.disconnect()
                    navController.navigate("profiles") {
                        popUpTo("connected") { inclusive = true }
                    }
                }
            )
        }
    }
}

@Composable
fun ConnectedScreen(viewModel: MainViewModel, onDisconnect: () -> Unit) {
    var selectedTab by remember { mutableStateOf(0) }

    Scaffold(
        bottomBar = {
            NavigationBar {
                NavigationBarItem(
                    icon = { Icon(Icons.Default.InsertDriveFile, contentDescription = "Terminal") },
                    label = { Text("Terminal") },
                    selected = selectedTab == 0,
                    onClick = { selectedTab = 0 }
                )
                NavigationBarItem(
                    icon = { Icon(Icons.Default.Folder, contentDescription = "Explorer") },
                    label = { Text("Explorer") },
                    selected = selectedTab == 1,
                    onClick = { selectedTab = 1 }
                )
            }
        }
    ) { padding ->
        Box(modifier = Modifier.padding(padding)) {
            if (selectedTab == 0) {
                TerminalScreen(viewModel = viewModel, onDisconnect = onDisconnect)
            } else {
                ExplorerScreen(viewModel = viewModel, onDisconnect = onDisconnect)
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TerminalScreen(viewModel: MainViewModel, onDisconnect: () -> Unit) {
    val terminalOutput by viewModel.terminalOutput.collectAsState()
    var command by remember { mutableStateOf("") }
    
    val scrollState = rememberScrollState()
    LaunchedEffect(terminalOutput) {
        scrollState.animateScrollTo(scrollState.maxValue)
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Terminal") },
                actions = {
                    Button(onClick = onDisconnect) {
                        Text("Disconnect")
                    }
                }
            )
        }
    ) { padding ->
        Column(modifier = Modifier.padding(padding).fillMaxSize()) {
            Text(
                text = terminalOutput,
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth()
                    .verticalScroll(scrollState)
                    .padding(8.dp),
                style = TextStyle(fontFamily = FontFamily.Monospace)
            )
            Row(modifier = Modifier.fillMaxWidth().padding(8.dp), verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(
                    value = command,
                    onValueChange = { command = it },
                    modifier = Modifier.weight(1f),
                    placeholder = { Text("Enter command") }
                )
                Spacer(modifier = Modifier.width(8.dp))
                Button(onClick = {
                    viewModel.sendTerminalCommand(command)
                    command = ""
                }) {
                    Text("Send")
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ProfilesScreen(viewModel: MainViewModel, onAddProfile: () -> Unit, onConnect: (ConnectionProfile) -> Unit) {
    val profiles by viewModel.profiles.collectAsState(initial = emptyList())
    val connectionError by viewModel.connectionError.collectAsState()
    val isConnecting by viewModel.isConnecting.collectAsState()
    val snackbarHostState = remember { SnackbarHostState() }

    LaunchedEffect(connectionError) {
        connectionError?.let {
            snackbarHostState.showSnackbar("Connection Error: $it")
            viewModel.clearError()
        }
    }

    Scaffold(
        topBar = { TopAppBar(title = { Text("SSH Profiles") }) },
        snackbarHost = { SnackbarHost(snackbarHostState) },
        floatingActionButton = {
            FloatingActionButton(onClick = onAddProfile) {
                Text("+")
            }
        }
    ) { padding ->
        Box(modifier = Modifier.padding(padding).fillMaxSize()) {
            LazyColumn(modifier = Modifier.fillMaxSize()) {
                items(profiles) { profile ->
                    Card(
                        modifier = Modifier.fillMaxWidth().padding(8.dp).clickable { onConnect(profile) }
                    ) {
                        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                            Column(modifier = Modifier.weight(1f).padding(16.dp)) {
                                Text(text = profile.name, style = MaterialTheme.typography.titleMedium)
                                Text(text = "${profile.username}@${profile.host}", style = MaterialTheme.typography.bodyMedium)
                            }
                            IconButton(onClick = { viewModel.deleteProfile(profile.id) }) {
                                Icon(Icons.Default.Clear, contentDescription = "Delete Profile")
                            }
                        }
                    }
                }
            }
            if (isConnecting) {
                CircularProgressIndicator(modifier = Modifier.align(Alignment.Center))
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AddProfileScreen(viewModel: MainViewModel, onPickKey: ((String) -> Unit) -> Unit, onBack: () -> Unit) {
    var name by remember { mutableStateOf("") }
    var host by remember { mutableStateOf("") }
    var username by remember { mutableStateOf("ubuntu") }
    var keyPath by remember { mutableStateOf("") }
    var passphrase by remember { mutableStateOf("") }

    Scaffold(
        topBar = { TopAppBar(title = { Text("Add Profile") }) }
    ) { padding ->
        Column(modifier = Modifier.padding(padding).padding(16.dp)) {
            OutlinedTextField(value = name, onValueChange = { name = it }, label = { Text("Profile Name") }, modifier = Modifier.fillMaxWidth())
            Spacer(modifier = Modifier.height(8.dp))
            OutlinedTextField(value = host, onValueChange = { host = it }, label = { Text("Host (IP)") }, modifier = Modifier.fillMaxWidth())
            Spacer(modifier = Modifier.height(8.dp))
            OutlinedTextField(value = username, onValueChange = { username = it }, label = { Text("Username") }, modifier = Modifier.fillMaxWidth())
            Spacer(modifier = Modifier.height(8.dp))
            OutlinedTextField(value = passphrase, onValueChange = { passphrase = it }, label = { Text("Passphrase (Optional)") }, modifier = Modifier.fillMaxWidth())
            Spacer(modifier = Modifier.height(8.dp))
            Button(onClick = {
                onPickKey { path -> keyPath = path }
            }) {
                Text(if (keyPath.isEmpty()) "Select Private Key" else "Key Selected")
            }
            Spacer(modifier = Modifier.weight(1f))
            Button(
                onClick = {
                    if (name.isNotBlank() && host.isNotBlank() && keyPath.isNotBlank()) {
                        viewModel.addProfile(name, host, username, keyPath, passphrase.takeIf { it.isNotBlank() })
                        onBack()
                    }
                },
                modifier = Modifier.fillMaxWidth()
            ) {
                Text("Save Profile")
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ExplorerScreen(viewModel: MainViewModel, onDisconnect: () -> Unit) {
    val files by viewModel.files.collectAsState()
    val currentPath by viewModel.currentPath.collectAsState()

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(currentPath) },
                actions = {
                    Button(onClick = onDisconnect) {
                        Text("Disconnect")
                    }
                }
            )
        }
    ) { padding ->
        LazyColumn(contentPadding = padding, modifier = Modifier.fillMaxSize()) {
            items(files) { file ->
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable {
                            if (file.isDirectory) {
                                viewModel.loadFiles(file.path)
                            }
                        }
                        .padding(16.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Row(modifier = Modifier.weight(1f)) {
                        Icon(
                            imageVector = if (file.isDirectory) Icons.Default.Folder else Icons.Default.InsertDriveFile,
                            contentDescription = null,
                            modifier = Modifier.padding(end = 8.dp)
                        )
                        Text(text = file.name)
                    }
                    IconButton(onClick = { viewModel.deleteFile(file.path) }) {
                        Icon(Icons.Default.Clear, contentDescription = "Delete")
                    }
                }
            }
        }
    }
}
