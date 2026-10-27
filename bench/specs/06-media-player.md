# Media Player

A simple video/audio player.

## Acceptance checklist

1. A main screen whose largest area is a `MediaPlayerElement` showing the current media (start with any https sample video URL, for example `https://download.blender.org/durian/trailer/sintel_trailer-480p.mp4`).
2. A transport bar under the player with Previous, Play/Pause, Stop and Next buttons (symbol icons), implemented with your own buttons rather than only the built-in transport controls.
3. A "Speed" button whose flyout offers 0.5x, 1x, 1.5x and 2x playback rates (a `MenuFlyout` with items).
4. A "Volume" button whose flyout contains a slider (0 to 100) bound to the volume.
5. A playlist panel on the right listing at least three media items (title and duration); clicking an item plays it.
6. A text showing the current item's title above the transport bar.
7. An "Open URL" button that opens a dialog with a text box for a media URL and adds it to the playlist.
8. ViewModel with CommunityToolkit.Mvvm for the playlist, current item, rate and volume; code-behind only where the media player API requires it.

<!-- controls: MediaPlayerElement; MenuFlyout; Slider; ListView|ItemsControl|ItemsRepeater; Flyout -->
