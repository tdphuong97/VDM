# VDM — design notes (tasteskill app run)

Style: framed-sheet (seed 24340796), light. Chosen because VDM is glanced at by non-technical
people on Windows 11: a tinted frame around a white sheet reads as a native Windows window.

## Inventory
- Objects: downloads — name, size (2 MB to 8 GB), bytes done, speed (0.5 to 35 MB/s on Vietnamese home
  fibre), time left, state, 1 to 16 connections each with its own byte range, source host, file or
  video/audio (quality, thumbnail, site), added and finished times.
- Magnitudes: a list of 5 to 60 items; 1 to 3 running at once; a new install has 0.
- People: friends, family, colleagues; Windows 10/11; open the window when a link is caught, a few
  times a week, for a minute.
- Jobs, most frequent first: watch it finish and open the file; pause/resume; paste a link; retry a
  failed one; pick video or audio-only for a YouTube link.
- Product's own things: name VDM and its mark (an arrow dropping into a bar split into parts); the
  download folder and the free space on its drive; the browsers that are connected.

## The screen's question
"What is downloading, and when will it be done?" — biggest block: the list, running rows first.
Finding line under the title: "2 file đang tải, 14,6 MB/giây · xong khoảng 14:52 · 1 file lỗi".
Main job one click away: pause/resume on each row; "Thêm link" as the one primary.

## Face
The frame tinted from the product's teal (#f1f6f6 instead of #f9fbfc); the sheet stays white.

## Signature element
The connection strip: each download's progress bar is cut into its connections' byte ranges, each
filling at its own speed (IDM's segment map). Large, with per-connection speeds, in the expanded row.
