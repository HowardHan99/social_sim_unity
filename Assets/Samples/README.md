# Social Environment Autonomous Navigation 2.0

Documentation: [sean.interactive-machines.com](https://sean.interactive-machines.com)

This is the [Unity](https://unity.com) project for SEAN: Social Environment for Autonomous Navigation

## Source Repositories

Other repositories for the project are:

  - Catkin Workspace: https://github.com/yale-sean/sim_ws

  - ROS project: https://github.com/yale-sean/social_sim_ros

  - Unity Project: https://github.com/yale-sean/social_sim_unity

  - Documentation: https://github.com/yale-sean/social_sim_docs


## Developer Information

### Generate documentation

```
cp README.md Documentation/index.md
docfx Documentation/docfx.json --force
```

Copy the documentation into the web documentation project.

First, set the web project directory:

```
export WEB_DIR=$HOME/src/yale/social_sim_docs
```

Then copy the `api` folder:

```
mkdir -p $WEB_DIR/static/api/unity
cp -r _site/api/* $WEB_DIR/static/api/unity/
cp -r _site/styles $WEB_DIR/static/api/
```


### Code Formatting and Linting

Try to keep the typical C# code style enforced by Visual Studio.

In case some files get committed or edited and no longer adhere to this style, the whole project can be re-styled using:

```
dotnet tool install -g dotnet-format
dotnet format social_sim_unity.sln
```


# Unity Render Streaming Multiplayer - SEAN 2.0

## Intro

### 1. Scene Setup

Navigate to the main multiplayer scene:
```
Assets/Scenes/SEAN/Outdoor_MP.unity
```

In the Hierarchy, locate the URS system:
```
URS → GameManager_URS → MultiplayerSample.cs
```

### 2. Launch Web Server

To enable web guest connections, run the web server:
```bash
.\webserver.exe
```

This allows web users to connect and participate in the multiplayer session.

## Configuration

### MultiplayerSample.cs Component

The `MultiplayerSample.cs` script is the main controller that manages the multiplayer session setup. Key configuration options include:

**Spawn Positions:**
- `Host Spawn Point`: Where the local host player spawns
- `Guest Spawn Point`: Where the guest connection handler spawns  
- `Remote Player Spawn Point`: Where web guest avatars appear in the host's scene

**Prefab Assignments:**
- `Prefab Local Player`: The avatar prefab used for the local host player
- `Prefab Host`: Connection handler prefab for host functionality
- `Prefab Guest`: Connection handler prefab for guest functionality

### Setting Custom Player Prefabs

#### For Local Players (Host):
1. In the Inspector for `MultiplayerSample.cs`
2. Assign your desired player prefab to the `Prefab Local Player` slot
3. Ensure the prefab includes the `PlayerController.cs` component

#### For Web Players (Guests):
1. Navigate to `Assets/Prefabs/Host_URS_WithWebAvatar.prefab`
2. Check the `Multiplay.cs` component
3. Assign your desired web player prefab to the `Prefab` slot

## Available Player Prefabs

### 1. PwMDPlayer_URS.prefab
- Wheelchair user avatar
- Located at: `Assets/Prefabs/PwMDPlayer_URS.prefab`

### 2. RobotPlayer_URS.prefab  
- Robot avatar with cube placeholder
- Located at: `Assets/Prefabs/RobotPlayer_URS.prefab`
- **Customization**: Expand the prefab to replace the cube with your custom 3D model

## PlayerController.cs - Core Movement System

The `PlayerController.cs` script handles player movement and camera control with realistic human-like constraints:

**Movement Features:**
- WASD movement with realistic walking speed (5 m/s)
- Smooth turning with A/D keys (90°/second)
- Mouse look with limited head rotation (±80° horizontal, ±60° vertical)
- Automatic reset if player falls out of bounds


## System Architecture

### Connection Flow

1. **Host Setup**: 
   - Local Unity editor acts as the host
   - Creates a `Multiplay` handler for managing incoming connections
   - Spawns local player with direct input control

2. **Guest Connection**:
   - Web users connect through the web server
   - Creates `SingleConnection` handler for each guest
   - Establishes WebRTC connection for video streaming and input transmission

3. **Data Synchronization**:
   - **Video Stream**: Host renders scene and streams to web guests via `VideoStreamSender`
   - **Input Stream**: Web guest inputs are captured and sent to host via `InputSender`
   - **Data Channels**: Custom messages (like username labels) sync via `MultiplayChannel`

### Key Components

#### SignalingManager
- Manages WebRTC connections between host and guests
- Handles connection lifecycle and data channel setup

#### Multiplay.cs (Host Side)
- Receives connection offers from web guests
- Instantiates remote player prefabs for each guest
- Manages video streaming to guests
- Processes input data from web users

#### SingleConnection.cs (Guest Side)  
- Handles single guest connection to host
- Receives video stream from host
- Sends input data to control remote avatar

#### MultiplayChannel.cs
- Custom data channel for sending non-input data
- Currently handles username/label synchronization
- Extensible for additional game state data

### Input Processing Pipeline

```
Web Guest Input → InputSender → WebRTC DataChannel → 
Host InputReceiver → PlayerController → Avatar Movement
```

1. **Web Capture**: Guest browser captures keyboard/mouse input
2. **Transmission**: Input data serialized and sent via WebRTC data channel
3. **Host Processing**: Host receives input and applies to guest's avatar
4. **Movement**: PlayerController translates input to realistic movement
5. **Video Feedback**: Updated scene rendered and streamed back to guest
