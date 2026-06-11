<div align="center">
  <img src="MediaTools.Presentation/optimizing.png" alt="Media Tools Logo" width="150"/>
  <h1>Media Tools</h1>
  <p><b>A powerful, all-in-one suite of media processing utilities built with WPF and .NET 9.</b></p>
</div>

---

## 📖 Overview

**Media Tools** is a comprehensive desktop application designed to handle a vast array of media manipulation tasks. Whether you need to compress large video files, download playlists from YouTube, enhance your photos, or record your screen, Media Tools provides a sleek, modern, and unified interface to get the job done quickly.

The application leverages the power of industry-standard tools like **FFmpeg** and **yt-dlp** under the hood, wrapping them in an intuitive UI built with MahApps.Metro.

---

## 🚀 Features & Tools

### 🎥 1. Video Compressor
Quickly compress large video files to save disk space without significant loss in quality. 
- **What it does:** Uses advanced encoding profiles to reduce video file sizes.
- **How to use:** Navigate to the **Video Compress** tab, import your video, select your target quality profile or target size, and click compress.

### ✨ 2. Video Enhancer
Improve the quality of your videos with automated enhancements.
- **What it does:** Upscales resolution, improves framerates, and applies color correction filters.
- **How to use:** Open the **Video Enhancer**, load a video, toggle the desired enhancement filters (e.g., upscale to 4K), and process.

### 🎧 3. Audio Enhancer
Clean up and boost your audio tracks.
- **What it does:** Removes background noise, normalizes volume, and enhances vocal clarity.
- **How to use:** Go to the **Audio Enhancer**, select your audio file, choose your noise reduction or volume normalization settings, and export.

### 🖼️ 4. Photo Enhancer
Bring your images to life with automated improvements.
- **What it does:** Applies sharpness, contrast adjustments, and upscaling to standard images.
- **How to use:** Select the **Photo Enhancer** tool, drag and drop your image, and apply the enhancements.

### 🔴 5. Screen Recorder
Capture your screen with ease.
- **What it does:** Records full screen or custom selected regions of your display along with system audio or microphone input.
- **How to use:** Open **Screen Recorder**, choose between "Full Screen" or "Region" (an overlay will let you drag to select an area), select your audio sources, and hit record.

### 🎞️ 6. Thumbnail Generator
Extract perfect thumbnails from your videos automatically.
- **What it does:** Scans a video and extracts high-quality frames to be used as thumbnails.
- **How to use:** Go to the **Thumbnail Generator**, import your video, set the timestamp or interval, and click generate.

### ⬇️ 7. YouTube Video Downloader
Download videos directly from YouTube, including entire playlists!
- **What it does:** Fetches videos from YouTube links. Supports parsing playlists and downloading multiple videos simultaneously.
- **How to use:** Navigate to **YouTube Video** under Video Tools, paste your URL, and click Search. You can select specific videos using the checkboxes, choose your preferred **Video Quality** (High/Medium/Low), and click download.

### 🎵 8. YouTube Audio Downloader
Extract high-quality audio tracks from YouTube.
- **What it does:** Downloads YouTube videos and automatically converts them to your preferred audio format (e.g., MP3). Supports batch playlist downloading.
- **How to use:** Navigate to **YouTube Audio** under Audio Tools, paste the URL, search, select your tracks, and hit download.

### ⚙️ 9. App Settings
Configure your workspace and preferences for the entire suite.
- **What it does:** Centralized configuration for output paths, hardware acceleration (GPU encoder detection), global hotkeys for screen recording, and Windows Toast notification settings.
- **How to use:** Navigate to **App Settings** at the bottom of the sidebar. You can run the hardware encoder detection here to ensure your GPU is being fully utilized by the media tools.

---

## 💻 Installation

### Using the Installer (Recommended)
We provide an automated installer that will set up Media Tools on your system.
1. Download the latest `MediaToolsInstaller.msi` from the Releases page.
2. Run the installer.
3. During setup, you can customize the installation and choose to add a **Desktop Shortcut** or **Start Menu Shortcut**.
4. Launch the app!

### First Launch
Upon launching the app for the first time, Media Tools will automatically detect and download required external dependencies (like FFmpeg) in the background. Please wait for the setup to complete.

---

## 🛠️ Development & Building from Source

Media Tools is built on the modern **.NET 9** framework.

### Prerequisites
- Visual Studio 2022 (v17.10+) or the .NET 9 SDK.
- Windows 10/11

### Build Instructions
1. Clone the repository: `git clone https://github.com/Mahmoud-ibrahim74/MediaTools.git`
2. Open `MediaTools.sln` in Visual Studio.
3. Set `MediaTools.Presentation` as your Startup Project.
4. Build and Run!

To build the setup installer, simply build the solution in `Release` configuration. The `MediaTools.Installer.wixproj` will automatically harvest the output files and generate an MSI.

---

## 👨‍💻 About the Developer

Developed with ❤️ by **Mahmoud Ibrahim**  
*Software Engineer & .NET Developer*

Let's connect!
- **LinkedIn:** [Mahmoud Ibrahim](https://www.linkedin.com/in/mahmoud-ibrahim74?utm_source=share_via&utm_content=profile&utm_medium=member_android)
- **GitHub:** [@Mahmoud-ibrahim74](https://github.com/Mahmoud-ibrahim74)
- **WhatsApp:** [Message Me!](https://wa.me/201069903556)
- **Facebook:** [Mahmoud Ibrahim](https://www.facebook.com/Houda405/)
