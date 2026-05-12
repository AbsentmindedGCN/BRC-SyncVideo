# SyncVideo
This is a mod for Bomb Rush Cyberfunk that allows players to watch movies and videos with friends, in-game.  
The plugin supports YouTube videos, along with MP4, WebM, AVI, MOV, M4V, and most MKV formats.

**NOTE: IF YOU ARE ON WINDOWS 11, YOU PROBABLY WILL NEED TO [DOWNLOAD THE HEVC CODEC](https://codecguide.com/media_foundation_codecs.htm) TO WATCH MKV VIDEOS.**  
Don't blame me, Microsoft is f-ing stupid and they want to sell you sh-t that came free with your f-ing ~~Xbox~~ Windows 10.

**NOTE: IF YOU ARE ON LINUX, I RECOMMEND USING [PROTON GE RTSP](https://github.com/SpookySkeletons/proton-ge-rtsp), OTHERWISE YOU'LL GET STUCK SYNCING.** 
This is an issue with how wine handels translating the codecs into Linux compatible calls.

## Features
- Synchronized video playback, with a phone app that has intuitive play, pause, seek controls
- Works off ACN's Lobby System, enabling "drop-in, drop-out" watch parties
- Support for Private and Public video lobbies for any map with a TV
- Immersive options, so you can hide the UI, chat, and player names
- YouTube video support, through [yt-dlp](https://github.com/yt-dlp/yt-dlp)
- [FFmpeg](https://ffmpeg.org/) support, for higher quality YouTube video playback
- Additional advanced options to support map developers, including screen positioning and spawning tools

---

## Usage / How-To
### Hosting a Lobby
- Open the "Sync Video" app on your phone
- Create a lobby
- Enter a video URL (CTRL+V in prompt)
- Press Enter to start playback

### Joining an Existing Lobby
- Open the Public Lobbies menu  
- Select a lobby 
- Join and watch

### Making a Lobby Private
- After creating a lobby, scroll down to "Lobby: Open"
- Change the option to "Lobby: Closed"
- Kick any viewers that aren't supposed to be there

### Adjusting Video Resolution
You can adjust the video resolution to a variety of options, but the higher resolutions will use more system resources.
There are two settings to control the resolution in the main config, **VideoRenderResolution** and **YouTubeResolution**.

**Resolution Options:**
- 1920×1080
- 1280×720 (YouTube default, recommended)
- 960×540
- 854×480 (MP4 video default, recommended)
- 640×360
- 426×240

If using higher YouTube resolutions, it is highly encouraged that viewers also enable **UseFFmpeg** for higher quality YouTube playback.
This option will make SyncVideo download a temporary copy of the video at the viewers selected resolution before starting playback.
Please note that while this enables much higher quality video playback, it is slower than streaming YouTube videos without using FFmpeg.

### Adjusting Screen Size/Location
Advanced users can adjust the size of their screen, in-game. To enable the "Screen Position" menu, set the option to True in the SyncVideo config.
After launching, you can enter the "Screen Position" menu, which is located within the lobby menu.
Please note that the screen size/location is only changed locally, and is not shared with any viewers.

### Debug & Advanced Options
If you run into an issue, or want to mess with some things under the hood while developing a map to support SyncVideo, use the SyncVideoAdvanced config.
This config includes options to change what objects the screen(s) will spawn on, as well as options to adjust the sync timings for the host and viewers.

---

## Special Thanks
- To the entire [yt-dlp](https://github.com/yt-dlp/yt-dlp) team <3
- To the entire [FFmpeg](https://github.com/ffmpeg/ffmpeg) team <3
- To [JasonOfTheStorm](https://github.com/jasonofthestorm), for his [VideoOnTV plugin](https://thunderstore.io/c/bomb-rush-cyberfunk/p/jasonofthestorm/VideoOnTV/) that inspired this project!

---

## FAQs
### > Where can I find good videos to watch?
Navigate to "YouTube.com" and search for the channel "[Nerrel](https://www.youtube.com/@Nerrel)" :)

### > Are YouTube livestreams supported?
No, not currently. Twitch.tv streams are not supported, either.

### > I'm making a map. How can I make my map compatible with SyncVideo?
Same way you would with [JasonOfTheStorm's VideoOnTV](https://thunderstore.io/c/bomb-rush-cyberfunk/p/jasonofthestorm/VideoOnTV/). Sync Video looks for objects in the Junk layer named "TV". If it finds one, it creates a screen at the object's origin. When making a map, make an object you want the screen to spawn on (I recommend a plane instead of a cube) and name it "TV" exactly, then set it to the "Junk" layer. You can scale the size of the TV object to change the spawned screen size.

### > Where are cached videos saved?
When using FFmpeg, videos downloaded are temporarily saved inside your plugin folder's cache folder. They are automatically deleted when a new video is selected or the lobby closes.

### > Why can't I watch some MKVs?
Because Micosoft is garbage. Windows 11 removed H.265 HVEC code support by default, which is the most popular MKV codec, because they want YOU to pay for it.
Don't give them any money. [Install it yourself instead from CodecGuide](https://www.codecguide.com/media_foundation_codecs.htm) instead.
I recommend downloading the [ZIP package containing all the installers](https://www.mediafire.com/file/301byh0e9ehxfc4/Media_Foundation_Codecs.zip/file) and installing all of them.

### > Why do some of my MPVs give me a Video URL Error?
MKVs use a LOT of different codecs and Sync Video only supports the most popular ones (H.264 & H.265). Your MKV is probably using a different codec.
If you're not sure what codec your video uses, you can check it by opening it in [VLC Media Player](https://www.videolan.org/) and pressing Ctrl+J.

### > My audio sounds really scratchy, how do I fix it?
If you're a viewer, either give it a movement for the video to sync with the host (it will get better after a couple seconds) or try leaving and rejoining the lobby.

### > Did this plugin mess up my BRC? Steam says it's still open but it's not!
This happens occasionally if you close the game without giving it a second to close/shutdown everything, resulting in FFmpeg still open. To prevent this, leave the lobby before closing the game, then close the game from the main menu.

### > There's a big white obstruction near the top of my screen!
Uninstall [JasonOfTheStorm's VideoOnTV](https://thunderstore.io/c/bomb-rush-cyberfunk/p/jasonofthestorm/VideoOnTV/).

### > My game crashed / I found a bug!
Sorry about that!! Send me the BepInEx log and I'll look into it. I'm in the [Freesoul Discord](https://discord.com/invite/freesoulbrc).

---

## License for SyncVideo
© Absentminded 2026 / Relevant Parties
Sync Video is provided, "as is", without any warranty of any kind, under the GNU Lesser General Public License (LGPL) v2.1 or later.

## Third-Party Licenses
This plugin includes FFmpeg, a multimedia framework.
FFmpeg is licensed under the GNU Lesser General Public License (LGPL) v2.1 or later.
* FFmpeg © the FFmpeg developers
* Source: https://ffmpeg.org/
* License: https://www.gnu.org/licenses/lgpl-2.1.html
A copy of the license is provided in the distribution.
