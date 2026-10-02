# Capture pipeline

1. Enumerate current monitors with `EnumDisplayMonitors`, `GetMonitorInfo`, display-device names, bounds, primary status, and effective DPI.
2. Normalize the selected mode into one display, one clipped physical-pixel region, or the virtual-desktop bounding rectangle.
3. Start one `ddagrab` source per required DXGI output.
4. Region mode applies `video_size`, `offset_x`, and `offset_y` relative to its display.
5. All-display mode downloads BGRA frames and uses `xstack` with physical layout offsets and black fill.
6. Convert to YUV420P, enforce even output dimensions, and encode with Media Foundation H.264.
7. On stop, send `q`, wait for MP4 finalization, and report nonzero exit status and recent diagnostics.

FFmpeg's capture source performs frame pacing. Its source does not expose an unbounded managed frame queue, and the application does not allocate frame-sized managed arrays. A future frame that cannot be delivered at the configured rate is dropped or duplicated within FFmpeg's bounded capture path; the current FFmpeg text protocol does not expose a stable per-source drop counter, so the UI reports zero rather than inventing a value.
