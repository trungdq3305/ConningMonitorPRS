# ConningMonitorPRS — CLAUDE.md

## Tổng quan dự án

Ứng dụng WinForms hiển thị dữ liệu hàng hải thực thời gian thực (conning display) cho tàu thủy.  
Đọc dữ liệu từ các thiết bị qua cổng COM (NMEA sentences), xử lý báo động, và hiển thị lên màn hình chính.

**Stack:** .NET 8 · WinForms · C# 12 · GDI+ · `System.IO.Ports` · `System.Windows.Forms.DataVisualization`  
**Build:** `dotnet build` (target `net8.0-windows`)  
**Entry point:** `Program.cs` → `MainForm`

---

## Cấu trúc thư mục

```
ConningMonitorPRS/
├── Core/
│   ├── Data/
│   │   └── ConningDataHub.cs        # Singleton thread-safe data store + Snapshot (bao gồm GPS raw
│   │                                 # lat/lon, fix quality, satellite list, GSA DOP)
│   ├── Geo/
│   │   ├── GeoMath.cs                # Haversine DistanceMeters() / BearingDeg() / OffsetMeters()
│   │   │                             # (xấp xỉ equirectangular, dùng cho track/target/wake-trail)
│   │   └── UtmConverter.cs           # WGS84 lat/lon → UTM (zone/band/easting/northing)
│   └── Models/
│       ├── AppConfig.cs             # JSON-serialisable config (Tasks + alarm limits + vessel params
│       │                             # + Position Watch + Targets + DUO GPS [#if] + PRS-GNSS-01 + PRS-PQE-01)
│       ├── DeviceTask.cs            # TaskName, PortName, BaudRate, SentenceType
│       ├── SystemConfig.cs          # Static runtime config (IsLight, IsSimMode, WindMax, Loa,
│       │                             # DriftWatch*, Targets, DuoGps* [#if], Gnss*…, Pqe*…)
│       ├── TargetPoint.cs           # Name/Lat/Lon/Enabled + ExtraPoints (List<LatLon>, tối đa 3
│       │                             # đỉnh nữa) — up to 4 target, dùng cho Targets window
│       ├── LatLon.cs                # Lat/Lon đơn giản — dùng cho TargetPoint.ExtraPoints
│       ├── Alarm.cs / AlarmState.cs # Alarm model (binary Normal/Active/Acknowledged)
│       ├── Tag.cs                   # UI alarm tag
│       ├── HealthState.cs           # PRS-GNSS-01/PQE-01: graded Unknown/Healthy/Degraded/Warning/Invalid
│       ├── PersistenceTimer.cs      # PRS-GNSS-01: on-delay confirm timer, factored out of Alarm.Evaluate()
│       ├── RuleResult.cs            # PRS-GNSS-01/PQE-01: 1 channel verdict (RuleID/metric/threshold/evidence)
│       ├── GnssHealthStatus.cs      # PRS-GNSS-01: aggregate Overall + Channels(H1-H9) + Advisories
│       └── PqeStatus.cs             # PRS-PQE-01: aggregate Overall + Channels(Q1-Q8) + Advisories
├── Services/
│   ├── ComEngine.cs                 # Opens SerialPorts, fires OnDataReceived(portName, line)
│   │                                 # MRU/METEO ports: skip DataReceived + inactivity timeout,
│   │                                 # expose GetManagedPort() for MruService/MeteoService to borrow
│   ├── Parsing/
│   │   └── NmeaParserService.cs     # Parses HDT / MWV / CNTB / PRDID / PASHR / GGA / VTG / GSV / GSA
│   │                                 # Các event liên quan GPS đều mang theo portName (xem mục DUO GPS,
│   │                                 # hiện #if DUO_GPS_ENABLED). OnGgaExtendedParsed cho PRS-GNSS-01.
│   ├── AlarmEngine.cs               # Register / Evaluate / Ack alarms
│   ├── GnssHealthEvaluator.cs       # PRS-GNSS-01 — rule engine H1-H9, xem mục riêng cùng tên
│   ├── PqeEvaluator.cs              # PRS-PQE-01 — rule engine Q1-Q8, xem mục riêng cùng tên
│   ├── DpOaCore.cs                  # LÕI DP-OA — nhận status từ 2 module PRS, log transition, đẩy hub,
│   │                                 # tính Combined interpretation (mục 8 tài liệu)
│   ├── ConfigService.cs             # Load/Save config.json
│   ├── DataLogger.cs                # Periodic CSV logging, giữ log 90 ngày (LogRetentionDays) —
│   │                                 # + LogRuleEvent() cho PRS-GNSS-01/PRS-PQE-01/DpOaCore
│   ├── MeteoService.cs              # Optional meteo sensor (simulation-only, no real Modbus wired)
│   ├── MruService.cs                # Sole source of Roll/Pitch/Heave — Xsens MTi XBus binary
│   │                                 # (MTData2) on COM3 @115200. Sim mode: internal 100ms timer.
│   ├── Mru/
│   │   └── XsensMruProcessor.cs     # Heave DSP: bias EMA liên tục → LPF pre-filter → washout filter bậc 2
│   ├── XsensDetector.cs             # Registry scan (VID 0x2639) → auto-detect Xsens USB COM port
│   ├── SimulationEngine.cs          # Generates synthetic NMEA (GPS/WIND/HEADING/GPS2/GSV/GSA) at 100 ms
│   │                                 # when IsSimulationMode=true
│   └── SystemLogger.cs              # File-based info/error log
├── UI/
│   ├── Controls/
│   │   ├── ConningControl.cs        # PRIMARY: compass + velocity arrows + own-ship track/target
│   │   │                             # shapes + RANGE (GDI+ custom draw) — radar duy nhất trong app,
│   │   │                             # RadarControl.cs đã xoá (vai trò gộp hết vào đây, 2026-08-28)
│   │   └── TrendChartControl.cs     # Trend chart 3 mode (Motion/Nav/Wind), X-axis ghim cố định
│   │                                 # theo view window đã chọn. Hiển thị trong TrendsForm.
│   ├── Forms/
│   │   ├── MainForm.cs              # Root form — layout + service wiring + DUO GPS source selection
│   │   ├── ConfigForm.cs            # Password-protected settings form (6 tab: Alarm Limits,
│   │   │                             # COM Config, Alarm History, Position Watch, GNSS Health,
│   │   │                             # Position Quality — target editing moved out entirely
│   │   │                             # 2026-09-03, xem TargetsForm)
│   │   ├── DataListForm.cs          # DATA LIST window — raw NMEA theo từng port/task
│   │   ├── TargetsForm.cs           # Cửa sổ TARGETS — nơi DUY NHẤT sửa target (lên tới 300, 6 toạ
│   │   │                             # độ/target — 2026-09-03) — bearing/distance sống + trạng thái
│   │   │                             # Position Watch (drift alarm) + grid sửa riêng (không cần
│   │   │                             # login, nút ADD TARGET, xem mục Position Watch & Targets)
│   │   ├── TrendsForm.cs            # Cửa sổ TRENDS — chỉ còn TrendChartControl (radar đã gộp
│   │   │                             # hết vào ConningControl, 2026-08-28)
│   │   ├── SatelliteForm.cs         # Cửa sổ "GNSS HEALTH / SATELLITE STATUS" — số vệ tinh/SNR theo
│   │   │                             # constellation + DOP, + badge H1-H9 (PRS-GNSS-01) + dòng Combined
│   │   ├── PositionQualityForm.cs   # Cửa sổ POSITION QUALITY — badge Q1-Q8 (PRS-PQE-01) + dòng Combined
│   │   └── LoginForm.cs             # Admin login (password = SystemConfig.AdminPassword)
│   └── Theme/
│       └── Palette.cs               # All colours (dark/light), IsLight toggle
├── Config/
│   └── config.json                  # Persisted AppConfig (Tasks, limits, vessel params, watch, targets, DUO)
├── Logs/
│   └── crash_guard.txt              # Đếm số lần crash gần nhau — xem mục Reliability
└── ConningMonitorPRS.csproj
```

---

## Luồng dữ liệu

```
SerialPort (COM1: GPS, COM2: WIND, COM4: HEADING, COM6: GPS2)   SerialPort (COM3: MRU, XBus binary)
    ↓ ComEngine.OnDataReceived(portName, rawLine)                  ↓ MruService background Thread
    ↓ NmeaParserService.Parse(portName, raw)                        (borrows port via ComEngine.GetManagedPort())
    ↓ events: OnHeadingParsed / OnWindParsed /                      ↓ parse MTData2 frame → XsensMruProcessor.UpdateEuler()
    │  OnPositionParsed(port,lat,lon) / OnPositionRawParsed         ↓ OnMotionParsed(roll, pitch, heaveCm)
    │  (port,latDeg,lonDeg) / OnSpeedParsed(port,kn) /
    │  OnCogParsed(port,cog) / OnGpsQualityParsed(port,q) /
    │  OnSatellitesParsed(constellation,count,avgSnr) / OnGsaParsed
    ↓
    └── MainForm: mọi event GPS đi qua GpsSourceForPort(port) → cập nhật
        _gps1/_gps2 riêng biệt → SelectActiveGpsSource() chọn nguồn tốt hơn
        → PublishActiveGps() đẩy DUY NHẤT nguồn đang active vào ConningDataHub
        (xem mục "DUO GPS mode")
    ↓
    └──────────────→ ConningDataHub.Instance.UpdateNumericData() / UpdateGpsData() / UpdateGpsRaw() /
                      UpdateGpsFixQuality() / UpdateCog() / UpdateSatellites() / UpdateGsa()
    ↓ Timer 100 ms (MainForm._uiTimer)
    ↓ ConningDataHub.Instance.GetSnapshot()
    ↓ MainForm.UiTick() → cập nhật Label + ConningControl.Update() + Invalidate() + tính DriftDistance/GpsDuoDivergence
```

> `OnMotionParsed` của `NmeaParserService` (từ `$CNTB`/`$PRDID`/`$PASHR`/`$PHTRO`) vẫn parse được nhưng
> **không còn được subscribe** trong `MainForm` — Roll/Pitch/Heave nguồn duy nhất là `MruService`.
> DataHub hub key vẫn là `"R/P/H"` dù `DeviceTask.TaskName` cấu hình là `"MRU"` (xem `DataListForm.HubKeyForTask`).

Simulation mode: `SimulationEngine.Start(callback)` → gửi NMEA giả (GPS/WIND/HEADING/GPS2/GSV/GSA) vào `callback`
(thay cho COM thật). GPS2 giả lập lệch GPS1 ~13m để có thể test cảnh báo `AL_GPSDUO`.
`MruService.Start()` tự kiểm tra `SystemConfig.IsSimulationMode` — nếu true, chạy `System.Timers.Timer` 100ms nội bộ
sinh Roll/Pitch/Heave giả lập qua `XsensMruProcessor`, không đụng tới COM port.
**Lưu ý:** `SimulationEngine` phải được lưu vào field (`_simEngine`) để tránh bị GC thu hồi.

**Bug "POSITION hiện NO FIX mãi trong Simulation Mode" (2026-09-03) — port giả lập tra theo TaskName,
không hardcode literal:** trước đây `SimulationEngine.OnTick`/`SendGsv` gọi thẳng
`_callback?.Invoke("COM1", ...)`/`"COM2"`/`"COM4"`/`"COM6"` — đúng miễn là `config.json` đã lưu sẵn
đúng y các port đó cho từng task. Nhưng `MainForm.GpsSourceForPort(portName)` tra
`ConfigForm.Tasks.Find(x => x.PortName == portName)` — nếu không khớp task nào (VD người dùng từng
test tính năng auto-swap COM ở mục COM Config rồi lưu lại, khiến GPS/WIND cùng trỏ `COM3` chẳng hạn,
không còn task nào giữ `"COM1"`) thì trả về `null`, khiến `OnPositionParsed`/`OnPositionRawParsed`/
`OnSpeedParsed`/`OnGpsQualityParsed` đều `if (src == null) return;` — dữ liệu GPS giả lập bị silently
drop, không exception, không log, card POSITION kẹt "NO FIX" vĩnh viễn. **Fix:** thêm helper
`SimulationEngine.PortFor(taskName, fallback)` tra `ConfigForm.Tasks` theo **TaskName** (không phải
literal cố định) để lấy đúng `PortName` hiện đang cấu hình, fallback về literal cũ chỉ khi task đó
không tồn tại trong danh sách; `OnTick` cache `gpsPort`/`windPort`/`headingPort`/`gps2Port` 1 lần đầu
mỗi tick rồi dùng lại cho mọi sentence liên quan (kể cả 2 lời gọi `SendGsv` giờ nhận thêm tham số
`port`). Nhờ vậy Simulation Mode luôn gửi đúng port bất kể `config.json` đang lưu gì — không còn phụ
thuộc port thực tế phải trùng khớp 4 literal mặc định. (File `config.json` trong `bin/Debug/.../Config/`
cũng được sửa lại 1 lần cho khớp default: GPS=COM1/WIND=COM2/MRU=COM3/HEADING=COM4/aux data=COM5/GPS2=COM6.)

**Chu kỳ đi thẳng/quay (2026-08-28)** — trước đây heading chỉ dao động ngẫu nhiên ±0.2°/tick (gần như
đứng yên, không có cú quay thật nào) và lat/lon **hoàn toàn tĩnh** (SOG random nhưng tàu không thực
sự di chuyển) — không đủ để test dư ảnh khi quay (`ConningControl.DrawShipTrail`) hay vệt track của
`ConningControl`. Đổi sang state machine 2 pha lặp lại:
- **Đi thẳng (`StraightLegSeconds`=8s):** heading gần như cố định (jitter nhỏ ±0.1°/tick) — ROT≈0,
  dư ảnh phải **không hiện** trong pha này (test đúng nhánh guard "không quay thì ẩn").
- **Quay (`TurnSeconds`=6s):** heading đổi mượt ±30-70° (ngẫu nhiên mỗi lần) theo easing cosin
  (`0.5 − 0.5·Cos(frac·π)`, tăng/giảm tốc mượt như 1 lệnh bẻ lái thật) — ROT rõ rệt, dư ảnh phải
  **hiện rõ, quét theo cung** trong pha này.
- **Vị trí GPS** giờ được dead-reckon thật mỗi tick từ heading+SOG hiện tại (Δlat/Δlon từ
  `distM=SOG(m/s)×dt`, xấp xỉ equirectangular) thay vì đứng yên — POSITION card đổi liên tục, track
  trail của `ConningControl` có đường đi thật để vẽ. GPS2 (COM6, DUO mode) vẫn lệch GPS1 ~13m như cũ,
  bám theo cùng vị trí đang di chuyển mỗi tick.

---

## Cổng COM và NMEA

| STT | TaskName | Port mặc định | Baud   | Header/Protocol   | Dữ liệu                        |
|-----|----------|---------------|--------|-------------------|--------------------------------|
| 1   | GPS      | COM1          | 9600   | $GPVTG / $GPGGA / $GPGNS / $GPGSV / $GPGSA | COG, SOG (knots), Lat/Lon, fix quality, vệ tinh |
| 2   | WIND     | COM2          | 4800   | $MWV / $WIMWV     | Wind speed (m/s), direction (°)|
| 3   | MRU      | COM3          | 115200 | XBus MTData2 (bin)| Roll, Pitch, Heave (cm) — xem `MruService` |
| 4   | HEADING  | COM4          | 4800   | $HEHDT / $HEHCR   | True heading (°)               |
| 5   | aux data | COM5          | 4800   | —                 | Dự phòng                       |
| 6   | GPS2     | COM6          | 9600   | $GPVTG / $GPGGA   | Nguồn GPS dự phòng (DUO mode) — chỉ dùng khi `SystemConfig.DuoGpsEnabled=true` |

`$--GNS` (GNSS Fix Data) parse cùng vị trí field lat/lon như `$--GGA` (`p[2..5]`) — một số GPS đa chòm
sao chỉ gửi GNS, không gửi GGA. Không map mode-indicator (`p[6]`, vd `"ANN"`) sang fix-quality — nhánh
GNS chỉ fire `OnPositionParsed`/`OnPositionRawParsed`, không fire `OnGpsQualityParsed`.

`$--HCR` (la bàn con quay Yokogawa, vd `$HEHCR,056.0,A,N,00.0*hh`) parse cùng field `p[1]` như `$--HDT`.
Cùng thiết bị này còn phát `$--HRC` (không có dấu phẩy giữa sentence ID và giá trị) ở tốc độ nhanh hơn —
**không** parse sentence đó (không an toàn để đoán field boundary trên định dạng không delimiter).

**COM3 (MRU)** không còn là NMEA text — là giao thức nhị phân Xsens XBus (frame `FA FF 36 LEN [items] CS`).
`ComEngine` mở port này giống các port khác nhưng **không** subscribe `DataReceived` và bỏ qua inactivity
timeout (giống `METEO`); `MruService` tự mượn port qua `GetManagedPort()` trên 1 background thread riêng.
`ComEngine` set `ReadTimeout=5000ms`, `WriteTimeout=500ms`, `DtrEnable=false`, `RtsEnable=false`,
`ReadBufferSize=8192` riêng cho task `"MRU"`.

Các sentence NMEA legacy vẫn parse được trong `NmeaParserService` (backward-compat nếu cần nối lại NMEA AHRS
qua COM3): `$CNTB`, `$PRDID`, `$PASHR`, `$PHTRO`, `$HEHDT`.
Checksum NMEA (`*XX`) được xác thực trước khi parse.
`DeviceTask.SentenceType` có thể lọc theo loại sentence (comma-separated, so sánh suffix).

`$GSV` được gộp qua nhiều sentence (msgNum/totalMsgs) theo từng constellation (nhận diện qua 2 ký tự talker:
GP/GL/GA/GB-BD/GQ/GI/GN) trước khi fire `OnSatellitesParsed`. `$GSA` field 2 (mode2) = fix type (1/2/3),
field 15-17 = PDOP/HDOP/VDOP.

---

## Giao diện chính (MainForm)

**Bố cục:** `TableLayoutPanel` — 1 cột trái (~1/3) + 1 cột phải (~2/3).

### Cột trái (Panel thông tin)
| Thẻ              | Nội dung                                  |
|------------------|-------------------------------------------|
| POSITION         | Lat/Lon (định dạng `DD°MM.mmm'NS`) từ nguồn GPS đang active. **Click để đổi ⇄ UTM** (icon "⇄" màu accent `Palette.ClickHint`, tooltip) |
| SPEED            | SOG (knots) từ nguồn GPS đang active. **Click để đổi ⇄ m/s** |
| HEADING          | True heading từ $HEHDT (°)               |
| WIND SPD/DIR     | Speed (m/s) + Direction (°)               |

### Thanh dưới (bottom bar)
| Nút              | Mở                                        |
|------------------|--------------------------------------------|
| ⚙ SETTINGS       | `ConfigForm` (yêu cầu login admin)        |
| 📋 DATA LIST      | `DataListForm` — raw NMEA từng port        |
| 🛰 GNSS HEALTH    | `SatelliteForm` — PRS-GNSS-01 (2026-09-07, nút mở lại sau khi bị ẩn — xem mục "PRS-GNSS-01") |
| 📶 POSITION QUALITY | `PositionQualityForm` — PRS-PQE-01 (2026-09-07, xem mục "PRS-PQE-01") |

TARGETS **không còn nút riêng ở bottom bar** — mở bằng cách bấm trực tiếp vào mini Targets card ở cột
phải (xem bên dưới), cursor đổi thành tay + tooltip khi hover, cùng kiểu tương tác với
`Palette.ClickHint`/`⇄` đã dùng cho card POSITION/SPEED (`WireOpenClick()`, tương tự
`MakeCardClickable()` nhưng nhận `Control[]` thay vì ép kiểu `Label` vì `DataGridView` không phải
`Label`). RADAR/TRENDS mở bằng 1 `Button` riêng (`BuildRadarTrendsButtonRow`) trong cột phải — xem
mục "Cột phải" ngay dưới về lý do không còn hình radar thu nhỏ ở đây nữa.

Nút "🛰 SATELLITES" (mở `SatelliteForm`) từng bị bỏ khỏi bottom bar — **đã thêm lại (2026-09-07)** dưới
tên "🛰 GNSS HEALTH" khi `SatelliteForm` được mở rộng cho PRS-GNSS-01 (xem mục "PRS-GNSS-01"), vì module
health mới cần 1 lối vào UI thật để người dùng xem được. Không còn là mục trong "Các lỗi đã biết".

Cả 3 form phụ (DataListForm/TargetsForm/TrendsForm) đều **non-modal, tự chủ**: có `Timer` riêng
đọc thẳng từ `ConningDataHub.Instance.GetSnapshot()`, không phụ thuộc `MainForm._uiTimer`. Chart/track buffer
của `TrendsForm` **không giữ lại lịch sử nền** — đóng cửa sổ rồi mở lại sẽ bắt đầu tích luỹ lại từ đầu.

### Cột phải
`BuildRightPanel()` chia tiếp thành 2 phần theo tỉ lệ **76% / 24%** (`TableLayoutPanel`, không phải
`SplitContainer` — không cần kéo-thả, tỉ lệ cố định):
- **76%:** `ConningControl` — xem phần chi tiết bên dưới.
- **24% (mini side panel, `BuildMiniSidePanel()`):** chia dọc 4 hàng: hàng 1 **Absolute** (96px, tiles),
  hàng 2 **Absolute** (34px, nút Radar/Trends), hàng 3 **Percent 55%** (mini trend chart), hàng 4
  **Percent 45%** (targets).
  - **Hàng 1 (96px, `BuildMiniMotionTiles`):** 3 ô ROLL/PITCH/HEAVE, dựng bằng cách gọi thẳng
    `BuildDataCard()` — chính method đã kiểm chứng cho POSITION/SPEED/HEADING/WIND (cùng `CardPanel`,
    cùng cơ chế auto-scale font qua `card.Resize`, cùng border vẽ trong `card.Paint`) — thay vì viết
    lại 1 bản UserControl mới. **2 lần viết tay riêng đều bị trắng số** (lần 1 dùng `ClockLabel` nền
    đặc, lần 2 copy tay công thức `Label`+`Transparent` của `BuildDataCard` nhưng vẫn không lên số) —
    không xác định được nguyên nhân chính xác dù đã soát kỹ, nên chuyển sang gọi thẳng
    `BuildDataCard()` (đã chạy thật cho 4 card khác) thay vì dựng lại lần 3. Màu chữ ghi đè sau khi
    tạo (`_miniRollVal.ForeColor = Palette.SeriesRoll` v.v.) để khớp màu series trên trend chart.
  - **Hàng 2 (34px, `BuildRadarTrendsButtonRow`):** chỉ 1 `Button` ("RADAR / TRENDS ⤢") → mở
    `TrendsForm`. **Trước đây (đến 2026-08-28) là 1 instance `RadarControl` thu nhỏ** (cùng class dùng
    trong `TrendsForm`, cập nhật mỗi tick 100ms) — bị bỏ theo yêu cầu người dùng sau khi 3 góc DP-
    reference (RANGE/WIND/DRIFT, xem mục "3 góc kiểu DP reference" trong `ConningControl`) được thêm
    nhầm vào đây thay vì vào `ConningControl` chính; thay vì sửa lại chỗ đặt, người dùng chọn bỏ hẳn
    hình radar thu nhỏ này (dư thừa/trùng lặp với `ConningControl` giờ đã tự mang đủ 3 góc đó) và chỉ
    giữ lại đường tắt mở `TrendsForm`. `RadarControl` class vẫn còn nguyên, vẫn dùng trong `TrendsForm`
    — chỉ không còn instance nào trong `MainForm` nữa.
  - **Hàng 3 (Percent 55%, `BuildMiniTrendChart`, 2026-08-28):** 1 instance `TrendChartControl`
    (cùng class dùng trong `TrendsForm`), khoá cứng `TrendMode.Motion` (constructor mặc định đã ở mode
    này, không gọi `SetMode` lại), **không có toolbar** chọn mode/khung thời gian (chỉ xem nhanh, đổi
    mode/khung giờ phải mở `TrendsForm` đầy đủ) — `SetViewWindow(0.5)` cố định 30 giây (ngắn hơn mặc
    định 1 phút của `TrendsForm` — panel này hẹp hơn nhiều, 1 phút dữ liệu ~10Hz dồn vào bề ngang nhỏ
    đọc như 1 vệt rối, 30s giãn cùng lượng điểm ra thưa hơn nên dễ đọc hơn). `UiTick` gọi
    `PushMotionData(RollDeg,PitchDeg,HeaveCm)` +
    `Render()` mỗi 100ms, y hệt cadence `TrendsForm.Poll()`. **Đây là điều ngược lại quyết định "cố
    tình không nhúng chart" trước đó** (xem lịch sử ngay dưới) — người dùng yêu cầu thẳng nhúng vào,
    chấp nhận đánh đổi gấp đôi chi phí render/CPU của 1 `Chart` control song song với bản trong
    `TrendsForm`. Vẫn cần `System.Data.SqlClient` PackageReference (xem mục "Radar/Trends window") vì
    dùng chung `Chart.OnPaint` — không phải nguy cơ mới, chỉ là instance thứ 2 chạy cùng workaround.
  - **Hàng 4 (Percent 45%, `BuildMiniTargetsCard`/`RefreshMiniTargets`):** **không phải `DataGridView`**
    — thử `DataGridView` trước (rối/chật khi đủ 4 target, phần đuôi trống trông như vỡ layout), rồi thử
    1 `Label` monospace `Consolas` mỗi dòng (căn cột bằng padding chuỗi — đọc vẫn "kỹ thuật"/khô, không
    hợp phong cách Segoe UI của cả app). Bản hiện tại: **4 `Panel` dòng dựng sẵn** (`_miniTargetRows`,
    kiểu `(Panel Row, Label Name, Label Brg, Label Dist)[4]`), mỗi dòng 3 `Label` Dock Left/Fill/Right
    riêng (Name/Bearing/Distance) — cột thật thay vì chuỗi căn tay, font Segoe UI khớp toàn app, màu
    Bearing dùng `Palette.ClickHint` để tách biệt trực quan khỏi Name/Distance, có đường kẻ phân cách
    mảnh vẽ tay dưới mỗi dòng (`row.Paint`) cho dễ quét mắt. Chỉ bật `Visible` đúng số target đang
    `Enabled` — cùng kiểu tái dùng-tile-ẩn/hiện đã chứng minh ở `TrendsForm._readouts` ("up to 3 tiles,
    reused across modes, unused ones hidden"), nhờ vậy né luôn lỗi nhấp nháy DataGridView-không-double-
    buffer (chỉ gán `.Text`/`.Visible`, không `Rows.Clear()`/`Add()`). Container là **`Panel` thường**
    (không phải `TableLayoutPanel`) với 4 dòng con `Dock=Top` thêm vào theo **thứ tự ngược** (dòng 0
    thêm SAU CÙNG để nổi lên trên cùng — same rule đã dùng ở `BuildLeftPanel`'s `lblShip`) — cố tình
    tránh `TableLayoutPanel` vì đó chính là nguồn gốc lỗi "hàng cuối bị giãn ra hút hết khoảng trống dư
    của `Dock=Fill`" đã gặp ở bản trước (Target 4 trôi lơ lửng cách xa 3 hàng trên); `Panel`+`Dock=Top`
    không có kiểu auto-stretch đó, khoảng trống dư chỉ đơn giản là nền trống bên dưới dòng cuối cùng.
    Mỗi dòng cao **36px** (trước 30px, tăng theo yêu cầu cho thoáng/dễ đọc hơn), và có **1 hàng tiêu đề
    cột NAME/BRG/DIST** ở trên cùng (`colHeader`, thêm sau vòng lặp dòng để nổi lên trên cùng, cùng
    chiều rộng Left/Fill/Right với các dòng dữ liệu bên dưới nên thẳng cột) — chữ dim, đường kẻ dưới
    đậm hơn 1 chút (`Palette.BorderCard` thay vì `BorderPanel` của các dòng thường) để phân biệt rõ đây
    là header chứ không phải 1 dòng target. Refresh vẫn throttle **~500ms** (mỗi 5 tick UI) như thiết kế cũ, cho công thức
    `GeoMath.DistanceMeters`/`BearingDeg`, kèm 1 dòng trạng thái Position Watch (`_miniDriftLbl`). Cùng
    kiểu tiêu đề dạng nút bấm thật như nút Radar/Trends → mở `TargetsForm` (`OpenTargetsForm()`).

**Bug "mini Targets card bị nhấp nháy" (2026-09-03)** — người dùng báo phần Targets ở góc phải màn
hình chính nhấp nháy. Nguyên nhân: `RefreshMiniTargets` gán lại `.Text` cho `nameLbl`/`brgLbl`/
`distLbl`/`_miniDriftLbl` mỗi ~500ms — bearing/distance **thực sự đổi** gần như mỗi lần (tàu luôn di
chuyển, kể cả trong Simulation Mode), nên không có giá trị trùng lặp nào để `Control.Text`'s internal
no-op-nếu-không-đổi tự chặn lại — nhưng toàn bộ `Label`/`Panel` dựng bằng `new Label {...}`/
`new Panel {...}` trong `BuildMiniTargetsCard` (`listPanel`, mỗi `row`, `colHeader`, `nameLbl`,
`brgLbl`, `distLbl`, `_miniDriftLbl`) đều là control **thường, không double-buffer**, với các Label
còn dùng `BackColor = Color.Transparent` — đúng y hệt gốc lỗi nhấp nháy đã từng gặp và fix ở
`ClockLabel` (đồng hồ góc trên) và ở `TrendsForm`'s `ReadoutLabel`/`BufferedPanel`/`BufferedFlowPanel`
("Panel/FlowLayoutPanel không tự double-buffer... mọi cấp control chứa giá trị hay đổi đều cần double-
buffer, không chỉ Label lá"). **Fix:** thêm class `BufferedPanel` mới (double-buffer, không cần tự vẽ
nền — Panel mặc định đã đủ 1 khi double-buffer) dùng cho `listPanel`/mỗi `row`/`colHeader`; đổi
`nameLbl`/`brgLbl`/`distLbl`/`_miniDriftLbl` từ `Label` sang `ClockLabel` sẵn có (double-buffer + tự
vẽ nền `BackColor` trong `OnPaintBackground`), đồng thời đổi `BackColor` từ `Transparent` sang đúng
màu nền thật của control cha (`Palette.CardBg` cho các Label trong row/listPanel, `Palette.CardFace`
cho `_miniDriftLbl` vì nó nằm trực tiếp trên `card` — `CardPanel` tô nền bằng `CardFace`, khác
`CardBg`) — bắt buộc phải đổi màu vì `ClockLabel.OnPaintBackground` tô đặc `BackColor` thật, không hỗ
trợ trong suốt như control mặc định. `hName`/`hBrg`/`hDist` (chữ tiêu đề, không bao giờ đổi sau khi
tạo) giữ nguyên `Label` thường — không có gì để nhấp nháy vì `.Text` chỉ gán 1 lần lúc dựng UI.

**Bug tiếp theo "mất đường kẻ phân cách giữa các dòng target" (2026-09-03, ngay sau fix nhấp nháy ở
trên)** — đổi `nameLbl`/`brgLbl`/`distLbl` từ `Label` (Transparent) sang `ClockLabel` (tô đặc
`BackColor`) để hết nhấp nháy, nhưng kéo theo tác dụng phụ: trước đây các Label này **trong suốt**
nên đường kẻ đáy mỗi dòng (`row.Paint`, vẽ tại `y = row.Height-1`) vẽ bởi chính `row` hiện xuyên qua
được; giờ chúng **tô đặc** toàn bộ vùng `Dock=Left/Right/Fill` của mình — với `Dock=Left`/`Right`/
`Fill`, vùng đó mặc định cao **bằng hết chiều cao dòng** (36px), nên đè kín luôn đường kẻ đáy mà
`row.Paint` vẽ ngay bên dưới — người dùng báo "sao mất luôn các đường kẻ của bảng". **Fix:** thêm
`Padding = new Padding(0, 0, 0, 1)` cho mỗi `row` — WinForms tôn trọng `Padding` khi layout các child
`Dock`, nên 3 Label giờ chỉ cao 35px (chừa đúng 1px đáy), để lộ lại đúng hàng pixel có đường kẻ. Áp
dụng y hệt lý luận cho `colHeader` nếu sau này đổi `hName`/`hBrg`/`hDist` sang `ClockLabel` (hiện tại
3 label này vẫn là `Label` Transparent nên đường kẻ header không bị ảnh hưởng, chưa cần `Padding`).

---

## ConningControl — Hiển thị Conning

File: `UI/Controls/ConningControl.cs`  
UserControl GDI+ hoàn toàn custom. `DoubleBuffered = true`, `ResizeRedraw = true`, `OnPaintBackground` → no-op.

### Các thành phần (theo spec)

| #  | Tên             | Mô tả                                                                 |
|----|-----------------|-----------------------------------------------------------------------|
| 1  | Vòng 360°/30°   | Style radar (nền tròn tô đặc + vòng khoảng cách đồng tâm + tick 30°, khớp `RadarControl`) **cố định** — N luôn ở trên đỉnh, ring không quay |
| 2  | Hình tàu        | Quay theo heading (mũi nhọn/đuôi phẳng, khung viền màu cam bo tròn góc — không tô đặc, xem 2026-09-03) quanh tâm ring — xem "Dư ảnh tàu" về lý do đổi từ mũi phẳng |
| 3  | Mũi tên 1       | Lateral velocity ở mũi tàu, bên cảng (port) — gắn theo tàu, quay theo hdg |
| 4  | Mũi tên 2       | Lateral velocity ở đuôi tàu, bên cảng (port) — gắn theo tàu, quay theo hdg |
| 5  | Mũi tên 3       | Lateral velocity ở đuôi tàu, bên mạn phải (stbd) — gắn theo tàu, quay theo hdg |
| 6  | Mũi tên 4       | Lateral velocity ở mũi tàu, bên mạn phải (stbd) — gắn theo tàu, quay theo hdg |
| 7  | Mũi tên 5       | Axial velocity — Cos(CMG−HDG)×SOG, âm → lùi (AFT) — gắn theo tàu, quay theo hdg |
| 8  | Mũi tên 6       | Axial velocity — Cos(CMG−HDG)×SOG, dương → tiến (FWD) — gắn theo tàu, quay theo hdg |
| 9  | Mũi tên wind   | Hướng gió theo **true bearing** (ring cố định/north-up), từ mép ring vào trong, màu cyan |
| 10 | Vector Z (heave) | Vòng tròn nhỏ tại tâm tàu — chấm đặc = heave dương (lên/ra khỏi mặt phẳng), dấu X = heave âm (xuống/vào trong mặt phẳng); quy ước chuẩn cho trục vuông góc với mặt phẳng nhìn từ trên xuống, cùng gốc toạ độ với mũi tên axial/lateral |
| 11 | Setting         | Nhập LOA, LOB, GPS offset (ConfigForm ▸ Alarm Limits), tính ROT và speed tại tâm |
| 11b | Dư ảnh (afterimage) | Bóng mờ hình tàu, ghi mẫu định kỳ mỗi 3s (giữ tối đa 10 mẫu ≈30s), mờ dần theo tuổi — hiện khi tàu quay VÀ/HOẶC di chuyển, ẩn khi đứng yên hoàn toàn |
| 12 | ~~Góc ZOOM~~ | **Đã gộp vào RANGE (2026-09-03)** — xem mục "Gộp ZOOM vào RANGE" bên dưới |
| 13 | Góc DRIFT (trên-phải) | Bearing + khoảng cách trôi dạt so với điểm tham chiếu Position Watch — chỉ hiện khi bật `DriftWatchEnabled` và có GPS fix |
| 14 | Góc RANGE (dưới-trái) | Nút `+`/`−` rời rạc (chỉ còn cách duy nhất đổi RANGE — lăn chuột đã bỏ, xem 2026-09-03) — thang đo khoảng cách thật (`{5,10,20,50,100,500,1000}` mét, thêm mốc 2026-09-03) DUY NHẤT cho lưới ô ly + own-ship track + target shapes; hình tàu/mũi tên/dư ảnh cũng theo RANGE nhưng qua trần theo bậc, xem mục "Trần theo bậc RANGE". Hộp to gấp đôi (2026-09-03). Port từ `RadarControl` (đã xoá) |
| 15 | Own-ship track | Vệt đường đi 30 phút, `PushTrackPoint()`/`DrawTrack()`, scale theo RANGE — port từ `RadarControl` |
| 16 | Target shapes | Đọc `SystemConfig.Targets`, vẽ chấm/đoạn thẳng/đa giác tuỳ số tọa độ (`Lat/Lon` + `ExtraPoints`), màu `Palette.RadarTargetShape` (magenta) |
| 16b | Báo hiệu target ngoài tầm (2026-09-03) | Target xa hơn RANGE đang chọn → chevron chĩa ra ngoài tại viền ring, đúng bearing thật + nhãn tên/khoảng cách, thay cho việc vẽ hình (đã ra ngoài tầm nhìn) — xem mục "Báo hiệu target ngoài tầm RANGE" bên dưới |

Góc dưới-trái (WIND) đã bị **bỏ hẳn** (2026-08-28, theo yêu cầu người dùng) — `DrawWindCorner()` xoá
khỏi `ConningControl.cs` hoàn toàn (không phải ẩn). Hướng+tốc độ gió vẫn đọc được qua card WIND
SPD/WIND DIR ở cột trái MainForm, và qua chính mũi tên gió #9 vẽ trên ring.

Không còn ô chữ HDG riêng bên dưới ring — heading chỉ thể hiện qua góc quay của hình tàu. Mọi
giá trị số trên mũi tên hiển thị **chỉ con số** (không nhãn, không đơn vị, không chữ hướng) —
hướng được thể hiện bằng chính hướng vẽ của mũi tên.

**Vector X/Y/Z:** mũi tên axial (X, dọc trục) và lateral (Y, ngang trục) đã có sẵn từ trước; heave
(Z) không thể vẽ thành mũi tên nằm trên mặt phẳng vì nó vuông góc với hướng nhìn từ trên xuống —
`DrawHeaveVector()` dùng quy ước vector-diagram chuẩn (chấm-trong-vòng tròn/dấu X-trong-vòng tròn)
thay vì mũi tên, vẽ tại đúng điểm gốc (0,0) local mà mũi tên axial cũng xuất phát từ đó, để cả 3
trục đọc như 1 vector 3 chiều chung 1 gốc. Không có ROT indicator hay gauge phụ nào khác được thêm.

### Công thức tính vận tốc

```
deltaRad  = (COG − HDG) × π/180
vLong     = Cos(deltaRad) × SOG          // dọc trục (axial), SOG đơn vị KNOT
vLat      = Sin(deltaRad) × SOG          // ngang trục (lateral tại GPS), KNOT
omega     = ROT × π/180 / 60             // rad/s (ROT đơn vị deg/min)
loa2      = LOA / 2                      // mét
vBow      = vLat + omega × (loa2 − gpsOffset) × (1/0.514444)   // lateral tại mũi, KNOT
vStern    = vLat − omega × (loa2 + gpsOffset) × (1/0.514444)   // lateral tại đuôi, KNOT
```

**Đơn vị:** `SOG` (và do đó `vLong`/`vLat`) là **knot** (khớp đơn vị mặc định của card SPEED — xem
`ConningDataHub.GpsSpeedKnot`, `MainForm._conning.Update(...)` truyền thẳng `snap.GpsSpeedKnot`
không đổi đơn vị). Số hạng `omega × (loa2 ± gpsOffset)` (ω rad/s × khoảng cách mét = **m/s** theo
vật lý) phải nhân thêm `1/0.514444` (≈1.9438) để đổi sang knot trước khi cộng vào `vLat` — **bug đã
tìm và sửa**: bản trước cộng thẳng số hạng m/s vào số hạng knot mà không đổi đơn vị, khiến phần đóng
góp của tốc độ quay (ROT) vào mũi tên lateral ở mũi/đuôi tàu (`vBow`/`vStern`) bị thiếu ~1.94 lần so
với giá trị thật — chỉ lộ rõ khi tàu đang quay (ROT≠0), nên dễ bị bỏ sót khi test bằng mắt lúc tàu
đi thẳng. Sửa bằng cách nhân hệ số đổi đơn vị ngay trong `ConningControl` (không đổi `_sog` sang m/s
toàn cục) để giữ nguyên độ dài mũi tên đã tinh chỉnh trực quan cho chuyển động thẳng (`vLong`/`vLat`).

ROT được tính trong `ConningDataHub` từ delta heading, smoothing α=0.3.

### Vẽ vòng la bàn
- Ring/ticks/nhãn (N/E/S/W/30°…) vẽ **cố định**, không transform xoay — N luôn ở đỉnh.
- **Style radar-hoá (2026-08-28):** theo yêu cầu người dùng "áp dụng UI mini radar sang radar
  chính", `DrawCompassRing()` được vẽ lại để giống hệt `RadarControl` — nền tròn tô đặc
  `Palette.RadarFace`, 3 vòng tròn khoảng cách đồng tâm `Palette.RadarGrid` (R×1/4, ×2/4, ×3/4),
  tick chỉ còn mỗi 30° (bỏ hẳn phân cấp 5°/10°/30°/90° cũ) dùng `Palette.RadarTick`/`RadarNorth`,
  nhãn số nguyên độ thô (`deg.ToString()`, VD "30"/"60"/"120"…) thay vì mã 2 chữ số hàng hải cũ
  (`deg/10` kiểu "03"/"06"), viền ngoài `Palette.RadarBorder`. Bỏ luôn tam giác đỏ spike ở N (thay
  bằng 1 tick N tô đậm màu `RadarNorth`, giống `RadarControl`). Nền toàn control cũng đổi từ
  `Palette.AppBg` sang `Palette.RadarBg` (`OnPaint`'s `g.Clear`), và halo viền chữ của
  `DrawValueLabel`/mũi tên đổi từ `Palette.AppBg` sang `Palette.RadarFace` cho khớp nền mới. **Các
  mũi tên vận tốc/heave/legend (X/Y/Z) giữ nguyên không đổi** — chỉ phần ring/nền/tick là radar-hoá,
  không phải viết lại toàn bộ control.
- **Lưới ô ly (2026-08-28):** theo ảnh chụp màn hình 1 hệ DP reference thật gửi kèm (panel bên trái
  có nền kẻ ô vuông mờ phủ khắp cả panel, không chỉ trong vòng tròn), thêm `DrawGraphPaperGrid()` —
  vẽ lưới đường ngang/dọc đều nhau phủ **toàn bộ control** (không chỉ trong ring). Gọi **sau** khi tô
  nền tròn (`FillEllipse` với `RadarFace`) nhưng **trước** vòng khoảng cách/tick/tàu — nhờ vậy lưới
  hiện xuyên qua cả phần đĩa tròn (đè lên `RadarFace`) lẫn phần góc vuông ngoài ring (đè lên
  `RadarBg`), khớp đúng layout trong ảnh gốc. Màu `Palette.RadarGrid` nhưng hạ alpha xuống 70/255
  (`Color.FromArgb(70, Palette.RadarGrid)`) để không át mất ring/tick/tàu vốn đã dùng cùng gam màu.
  Qua 2 lần chỉnh theo phản hồi người dùng về ý nghĩa của khoảng cách ô:
  1. Lần 1 — spacing = `Max(12, R/7) × zoom` (thuần pixel, co giãn theo ZOOM nhưng không có ý
     nghĩa mét thật nào).
  2. Lần 2 (hiện tại, "mỗi ô đúng theo kích thước ngoài đời 1 cách ước chừng") —
     **`ComputeNiceGridMeters(pxPerMeter, R)`**: quy đổi pixel→mét bằng chính chiều dài tàu đã vẽ
     (`pxPerMeter = (2×sl) / LOA`, `sl` đã nhân sẵn `ShipZoom` nên ZOOM tự động kéo theo mà không
     cần nhân riêng nữa), lấy 1 khoảng pixel mục tiêu (`Max(12, R/7)`) đổi ngược sang mét, rồi làm
     tròn kiểu "nice number" cổ điển cho trục biểu đồ (dãy 1-2-5-10×, ví dụ 17.3m → làm tròn 20m)
     để mỗi ô đọc như 1 khoảng cách tin được thay vì số lẻ ngẫu nhiên từ mật độ pixel thô. Giá trị
     mét/ô này hiển thị luôn ở dòng cuối hộp ZOOM (VD "20m") để người dùng biết mỗi ô đại diện bao
     nhiêu mét ngoài đời — kéo thanh ZOOM to tàu lên thì `pxPerMeter` tăng, ô lưới tính lại theo mét
     mới (có thể nhảy bậc 1-2-5-10 khi vượt ngưỡng) thay vì scale mượt tuyến tính theo pixel thô.
  3. Lần 3 — 2 chỉnh nhỏ theo phản hồi tiếp: **(a)** lưới dựng từ **tâm tàu `(cx,cy)`** ra 2 phía
     thay vì từ góc trên-trái control `(0,0)` — bug cũ khiến hoa văn lưới bị "trượt" quanh tàu mỗi
     khi spacing đổi (do ZOOM/LOA thay đổi), tâm tàu không neo cố định vào giao điểm lưới nào; giờ
     luôn có 1 đường kẻ dọc + 1 đường kẻ ngang đi thẳng qua đúng tâm tàu bất kể spacing bao nhiêu.
     **(b)** đường lưới đậm hơn 1 chút — alpha 70→100/255, độ dày pen 1f→1.3f.
  4. **Lần 4 — Gộp ZOOM vào RANGE (2026-09-03):** người dùng phản hồi "các đường kẻ ô ly và zoom
     và range nên đồng bộ" — vì lưới ô ly (tính theo LOA+ZOOM) và vòng tròn RANGE (tính theo
     `RangeMeters`) là **2 thang đo mét độc lập** (như thiết kế cũ đã ghi rõ ở mục "RANGE + own-ship
     track" bên dưới), cùng hiện trên 1 mặt phẳng nhưng số mét/ô lưới và số mét/vòng RANGE không
     khớp nhau — gây khó đọc. Qua `AskUserQuestion`, người dùng chọn phương án **"Gộp hẳn ZOOM vào
     RANGE"** (bỏ hẳn slider ZOOM riêng, chỉ còn RANGE điều khiển toàn bộ, kể cả kích thước tàu vẽ
     theo đúng tỉ lệ LOA/LOB thật so với RANGE). Thay đổi cụ thể trong `ConningControl.OnPaint`:
     - Xoá hẳn `_zoomLevel`/`ShipZoom`/`_zoomTrackRect`/`_draggingZoom`/`SetZoomFromY`/
       `DrawZoomControl` và phần xử lý kéo-thả trong `OnMouseDown`/`OnMouseMove`/`OnMouseUp` (3
       override này bị xoá hoàn toàn — không còn gì để kéo-thả nữa).
     - `pxPerMeter` giờ tính **trước** `sl`/`sw` (đảo ngược thứ tự so với trước — trước đây `sl` có
       trước rồi suy ra `pxPerMeter` từ nó): `pxPerMeter = (R×0.94) / RangeMeters` — **đúng công
       thức RANGE đã dùng cho `DrawTrack`/`DrawTargets`** (`scale = (r×0.94)/RangeMeters`), nên giờ
       lưới + tàu + track + target **dùng chung đúng 1 giá trị mét→pixel**, không còn 2 thang đo
       tách biệt nữa.
     - `sl = (LOA/2) × pxPerMeter`, `sw = (LOB/2) × pxPerMeter` — tàu vẽ **đúng tỉ lệ thật** so với
       RANGE đang chọn, thay vì luôn cố định `R×0.40`/`R×0.145` như khi còn ZOOM. Hệ quả tất yếu:
       RANGE càng nhỏ (50m) so với LOA (mặc định 100m) thì tàu vẽ **to hơn cả ring** (đúng vật lý —
       tàu 100m không thể "vừa" trong phạm vi quan sát 50m); RANGE càng lớn (10km) thì tàu co lại
       gần như 1 điểm — có `ShipMinHalfLenPx=10f`/`ShipMinHalfWidthPx=4f` làm sàn tối thiểu để tàu
       không biến mất hẳn (giống cách 1 target radar/AIS vẫn hiện icon nhỏ dù nhỏ hơn 1px theo đúng
       tỉ lệ).
     - Mũi tên lateral/axial + vòng heave + `DrawValueLabel` **bỏ hẳn tham số `zoom`** (trước nhân
       thêm vào độ dày nét/cỡ chữ) — độ dày/cỡ chữ giờ là hằng số cố định, không còn phóng to theo
       gì nữa (RANGE chỉ ảnh hưởng kích thước THẬT của tàu qua `sl`/`sw`, không ảnh hưởng độ dày nét
       vẽ). **Cập nhật ngay sau đó (Lần 6 bên dưới):** tham số `zoom` được thêm lại, nhưng lấy tự
       động từ RANGE (`visualZoom`, kẹp [0.6,1.8]) thay vì slider thủ công — xem Lần 6.
     - Lăn chuột (`OnMouseWheel`, trước đây chỉnh `_zoomLevel` ±0.1×/nấc) giờ **đổi RANGE preset**
       trực tiếp (lăn lên = zoom in = RANGE nhỏ hơn/chi tiết hơn, lăn xuống = RANGE lớn hơn) — giữ
       nguyên trải nghiệm "lăn chuột để zoom" quen thuộc, chỉ đổi cái nó điều khiển.
     - Hộp RANGE (`DrawRangeControl`, dưới-trái) cao thêm (38→52px) để chứa thêm dòng 3 hiển thị
       kích thước ô lưới hiện tại (VD "20m/sq") — dòng này trước đây nằm ở hộp ZOOM riêng (nay đã
       xoá), giờ gộp vào cùng hộp RANGE vì cùng 1 con số phái sinh từ `RangeMeters`.
     - **Việc không làm (lúc này):** không đổi khoảng preset của RANGE (`RangePresetsM`, vẫn 50m-10km,
       8 mức — **đổi sau đó ở Lần 7 bên dưới**, thành `{5,10,20,50,100,1000}`, 6 mức);
       không thêm giới hạn min/max mới cho LOA/LOB; không đổi vị trí các góc còn lại (DRIFT vẫn
       trên-phải, legend X/Y/Z vẫn trên-trái) — control giờ chỉ còn **3 góc** thay vì 4 (RANGE/DRIFT/
       legend), không phải 4 góc như mô tả cũ trước 2026-09-03.
  5. **Bug "các ô ly không thu phóng" (2026-09-03, ngay sau Lần 4) — `ComputeNiceGridMeters` tự
     triệt tiêu chính hiệu ứng zoom nó cần tạo ra:** người dùng gửi 2 ảnh chụp màn hình RANGE=10km
     và RANGE=50m — nhãn "2km/sq"/"10m/sq" đổi đúng, nhưng **lưới ô vuông trông giống hệt nhau ở cả
     2 mức RANGE** (thực ra gần như không thấy lưới ở cả 2 ảnh). Nguyên nhân toán học: hàm cũ
     `ComputeNiceGridMeters(pxPerMeter, R)` luôn **giải ngược** từ 1 mục tiêu pixel cố định
     (`targetPx = Max(12, R/7)`, không đổi theo RANGE) ra số mét tương ứng rồi làm tròn "nice" —
     nghĩa là `gridSpacingPx = gridMeters × pxPerMeter ≈ targetPx` **luôn xấp xỉ hằng số** bất kể
     `pxPerMeter` (và do đó RANGE) là bao nhiêu, theo đúng thiết kế gốc đã ghi ở Lần 2 phía trên
     ("ZOOM kéo `pxPerMeter` tăng thì ô lưới tính lại theo mét mới... thay vì scale mượt tuyến tính
     theo pixel thô" — tức bản thân thiết kế ban đầu vốn **cố tình** giữ pixel-spacing gần như cố
     định, chỉ đổi Ý NGHĨA mét của mỗi ô, giống cách vòng RANGE tròn cũng luôn vẽ ở bán kính pixel
     cố định `R×i/4` bất kể `RangeMeters`). Điều này đúng với slider ZOOM liên tục cũ, nhưng với 8
     mức RANGE rời rạc cách nhau 2-2.5× mỗi bước, việc tính lại luôn hội tụ về gần đúng `targetPx`
     mỗi lần — khiến lưới trông **y hệt nhau** giữa mọi mức RANGE, đúng như người dùng phản ánh.
     **Fix:** đổi hẳn cách chọn `gridMeters` — không giải ngược từ mục tiêu pixel nữa, mà dùng
     **1 giá trị mét cố định** tham chiếu theo `LOA` của tàu (`NiceRound(LOA)`, VD LOA=100m →
     100m/ô — "mỗi ô ly ≈ 1 chiều dài tàu"), rồi để `gridSpacingPx = gridMeters × pxPerMeter` tự
     nhiên thay đổi theo `pxPerMeter` (vốn đã tỉ lệ nghịch với RANGE) — RANGE nhỏ (zoom in) thì ô
     lưới **thật sự to ra** trên màn hình, RANGE lớn (zoom out) thì ô lưới **thật sự nhỏ lại**, đúng
     yêu cầu "các ô ly phải thu phóng theo zoom". `AutoCoarsenGridMeters(niceMeters, pxPerMeter,
     minCellPx=18f)` chỉ can thiệp làm tròn **lên** theo nấc 1-2-5-10× (không bao giờ xuống) khi
     RANGE lớn tới mức ô cỡ LOA sẽ vẽ dày đặc dưới `minCellPx` (18px) — thuần túy là lưới an toàn
     chống rối mắt ở RANGE lớn (VD 10km), không phải cơ chế chính điều khiển kích thước ô như hàm cũ.
     `ComputeNiceGridMeters` cũ bị xoá, thay bằng `NiceRound(v)` (thuần làm tròn 1-2-5-10×, tách khỏi
     logic pixel-target) + `AutoCoarsenGridMeters(...)` (bước lên nấc).
  6. **Mũi tên + số cũng thu phóng theo RANGE, có trần/sàn (2026-09-03, ngay sau Lần 5):** người
     dùng yêu cầu tiếp "các mũi tên và số cũng thu phóng theo luôn, đến 1 mức to hoặc nhỏ nhất định
     để dễ nhìn" — tại Lần 4, tham số `zoom` từng bị xoá hoàn toàn khỏi `DrawLateralArrow`/
     `DrawAxialArrow`/`DrawHeaveVector`/`DrawValueLabel` (độ dày nét + cỡ chữ trở thành hằng số cố
     định, không phóng to/nhỏ theo gì nữa). Giờ thêm lại tham số `zoom` cho cả 4 hàm này, nhưng
     KHÔNG lấy từ 1 slider ZOOM thủ công như bản cũ (đã gộp/xoá) — mà **tính tự động từ RANGE**:
     `visualZoom = Clamp(sl / (R×0.40), VisualZoomMin=0.6, VisualZoomMax=1.8)` (`ConningControl.
     OnPaint`, ngay sau khi tính `sl`) — so sánh `sl` (nửa chiều dài tàu đã vẽ, giờ theo tỉ lệ thật
     so với RANGE) với **giá trị cố định cũ `R×0.40`** (kích thước tàu mặc định trước khi có RANGE
     thật) để suy ra tàu đang "phóng to" hay "thu nhỏ" bao nhiêu lần so với mốc đó, rồi **kẹp lại
     trong khoảng [0.6, 1.8]** — đây chính là "đến 1 mức to hoặc nhỏ nhất định" người dùng yêu cầu:
     dù `sl` tại RANGE=50m có thể lớn hơn cả ring hàng chục lần, hay tại RANGE=10km co lại gần 1px,
     `visualZoom` không bao giờ vượt quá 1.8× hay dưới 0.6× — mũi tên/chữ số không bao giờ phình to
     che hết màn hình hay teo nhỏ tới mức không đọc được. `arrowScale`/`arrowMax` (độ dài mũi tên
     vận tốc) cũng nhân thêm `visualZoom` ngay tại chỗ tính (khác Lần 4 — lúc đó 2 biến này CHỈ phụ
     thuộc `R`, không phóng to/nhỏ theo gì). `DrawValueLabel` giữ tham số `zoom = 1f` mặc định cho
     mũi tên gió (`DrawWindArrow` không truyền `visualZoom` — neo theo true bearing trên ring cố
     định, không gắn theo tàu, nên cố tình đứng ngoài hiệu ứng zoom này, y như quy ước cũ trước Lần 4).
  7. **Đổi thang preset + to gấp đôi hộp + bỏ lăn chuột (2026-09-03, ngay sau Lần 6):** theo yêu cầu
     "ô range ở góc trái dưới làm to ra gấp đôi, để theo thang 5 mét, 10 mét, 20 mét, 50 mét 100 mét,
     1000 mét, bỏ tăng giảm bằng lăn chuột" — 3 thay đổi độc lập:
     - `RangePresetsM` đổi từ `{50,100,250,500,1000,2000,5000,10000}` (8 mức, 50m-10km) sang
       `{5,10,20,50,100,1000}` (6 mức) — thang gần hơn, dồn nhiều mức ở khoảng cận (5-100m) cho công
       việc cần nhìn sát, chỉ còn 1 mức xa (1000m) thay vì trải đều tới 10km. `_rangeIndex` mặc định
       chỉnh lại thành `3` (= 50m, tương đương vị trí "mức giữa" cũ là 500m trong mảng 8 phần tử).
     - `DrawRangeControl` — mọi kích thước nhân đôi nguyên khối: `boxW` 76→152, `boxH` 52→104,
       `btnSz` 16→32, `pad` 4→8, cỡ chữ 7.5pt→15pt, và mọi offset vẽ chữ/nút nhân đôi theo
       (`bx+4→bx+8`, `by+2→by+4`, `by+17→by+34`, `by+33→by+66`...) — to đều cả hộp lẫn chữ/nút bên
       trong, không phải chỉ phóng khung ngoài quanh chữ cũ.
     - `OnMouseWheel` override (đổi RANGE bằng lăn chuột, thêm lúc gộp ZOOM vào RANGE — xem Lần 4)
       **xoá hẳn** — RANGE giờ chỉ đổi được qua 2 nút `+`/`−` trong hộp (`OnMouseClick`), không còn
       cách nào khác.
- Tàu + mũi tên + heading line: quay bằng `RotatePoint(px, py, hdg, cx, cy)` (xoay điểm trong hệ toạ
  độ local của tàu — gốc tại tâm ring, −Y = mũi tàu — theo chiều kim đồng hồ), cùng quy ước với
  `RadarControl.DrawShip`. Mỗi điểm trên tàu/mũi tên được xoay riêng rồi mới vẽ, không dùng
  `g.RotateTransform` (để nhãn giá trị vẽ sau đó luôn thẳng đứng, không bị xoay theo).
- **Công thức đúng:** `rx = px·cos − py·sin`, `ry = px·sin + py·cos`. Bản trước dùng
  `rx = px·cos + py·sin`, `ry = -px·sin + py·cos` (transpose của ma trận xoay chuẩn) — công thức
  này **lật ngược Đông/Tây**: heading=90°(Đông) lẽ ra phải trỏ sang phải (khớp tick "E" bên phải
  ring) nhưng lại cho ra bên trái, trong khi heading=0°/180° (Bắc/Nam) tình cờ vẫn đúng nên lỗi
  không lộ ra ngay. Bug này tồn tại y hệt ở cả `ConningControl.RotatePoint` lẫn
  `RadarControl.DrawShip` — nhưng `RadarControl`'s heading-line/wind-arrow lại dùng công thức polar
  trực tiếp (`Sin`/`-Cos`) vốn đã đúng từ đầu, nên NGAY TRONG 1 RadarControl, hình tàu (sai) và
  đường heading (đúng) tự mâu thuẫn nhau — verify bằng cách so khớp với vị trí tick "E"/"W" đã vẽ
  đúng của chính `DrawCompassRing`.

### Dư ảnh tàu — xoay + di chuyển (afterimage, 2026-08-28)
Người dùng gửi 1 sprite sheet nhân vật chạy (nhiều khung hình đè lên nhau mờ dần) làm ảnh tham khảo,
yêu cầu tàu cũng để lại "dư ảnh" vài giây khi di chuyển để biết đã quay/đi qua hướng nào. Vì ring cố
định/north-up nên tàu trên `ConningControl` **không bao giờ đổi vị trí trên màn hình** (luôn ở đúng
tâm ring, hiển thị kiểu ego-centric chứ không phải bản đồ cuộn) — ban đầu chỉ replay lại **heading**
(góc xoay). Sau đó người dùng yêu cầu thêm cả phần **di chuyển thật dựa trên GPS** (bản build trước
tàu chỉ có dư ảnh khi quay, không có khi đi thẳng), nên `DrawShipTrail()` giờ replay **cả heading lẫn
vị trí GPS**:
- `UpdatePosition(latDeg, lonDeg)` — gọi **trước** `Update()` mỗi tick (thứ tự bắt buộc, xem comment
  tại chỗ gọi trong `MainForm.UiTick`) để `Update()` ghi đúng lat/lon của tick đó vào mẫu lịch sử.
- `DrawShipTrail()` gọi ngay **trước** `DrawShip()` (vẽ dư ảnh trước, tàu thật đè lên sau cùng). Với
  mỗi mẫu lịch sử: tính lệch mét từ vị trí lịch sử tới vị trí GPS **hiện tại** (xấp xỉ
  equirectangular, giống `RadarControl.DrawTrack`), đổi sang pixel bằng đúng `pxPerMeter` mà lưới ô
  ly đang dùng (cùng 1 thước đo — dư ảnh và lưới luôn khớp tỉ lệ nhau), rồi vẽ ghost ship tại
  `(cx+offsetX, cy+offsetY)` xoay theo `hdg` lịch sử — dùng đúng hình dạng thân tàu (`ShipLocalPoints`)
  và `RotatePoint()` y hệt `DrawShip()`, chỉ khác tâm xoay là vị trí ghost thay vì `(cx,cy)`. Không có
  fix GPS (`NaN`) thì offset = (0,0), tự động rơi về hành vi "chỉ replay heading".
- **Ghi mẫu (2026-08-28, đổi từ liên tục sang định kỳ):** bản đầu `Update()` ghi 1 mẫu **mỗi tick**
  (100ms) vào buffer trượt 2.5s, tạo hiệu ứng "vệt mờ" mượt nhưng biến mất rất nhanh. Theo phản hồi
  "dư ảnh lưu hình lại trên control luôn, với cường độ 1 lần vài giây", đổi sang **ghi mẫu định kỳ**:
  `Update()` chỉ thêm vào `_trail` khi `(now − _lastStampTime) ≥ StampIntervalSeconds` (3s), giữ tối
  đa `MaxStamps` (10) mẫu gần nhất (`RemoveAt(0)` khi vượt) — tổng thời gian lưu ảnh kéo dài tới
  `StampIntervalSeconds×MaxStamps ≈ 30s` thay vì chỉ 2.5s, và mỗi "dấu chân" tồn tại rõ rệt vài giây
  thay vì mờ dần liên tục theo từng tick. Alpha vẫn giảm dần theo tuổi nhưng tính trên cửa sổ ~30s này:
  `Clamp(55×(1 − age/(StampIntervalSeconds×MaxStamps)), 0, 55)`.
- **Guard quan trọng:** bỏ mẫu nếu **cả 2** điều kiện cùng đúng: `AngleDiffDeg(hdg, _hdg) < 1.5°`
  **và** khoảng lệch vị trí `< 2px`. **Bắt buộc phải có**: nếu tàu đứng yên hoàn toàn (không quay,
  không di chuyển), không có guard này thì nhiều polygon trong suốt xếp chồng đúng lên nhau ở cùng 1
  vị trí sẽ cộng dồn alpha, làm tàu tự nhiên đậm/tối hơn dù chẳng có gì thay đổi — trông như artifact
  render. Dùng "cả 2 cùng đúng" (không phải "1 trong 2") để tàu **chỉ đi thẳng không quay** vẫn để lại
  vệt (lệch vị trí đủ lớn dù heading không đổi), và tàu **chỉ quay tại chỗ không di chuyển** vẫn để
  lại vệt (heading đổi đủ nhiều dù vị trí không đổi) — đúng yêu cầu "dư ảnh do cả xoay lẫn di chuyển".
  `AngleDiffDeg()` chuẩn hoá hiệu 2 góc về [-180,180] để xử lý đúng wraparound (VD 359° vs 1° → hiệu
  2°, không phải 358°). (`minGapSeconds` guard cũ của bản ghi-liên-tục đã bỏ — không cần nữa vì bản
  thân việc ghi mẫu định kỳ 3s/lần đã tự nhiên giãn cách đủ xa, không cần lọc thêm.)

**Bug "vệt dư ảnh ở phía trước mũi tàu" (không phải bug toán học):** ngay sau khi thêm phần di chuyển
ở trên, người dùng báo vệt dư ảnh trông như nằm phía trước mũi tàu thay vì kéo dài sau đuôi. Kiểm tra
lại công thức offset bằng tay (cả hướng Bắc lẫn Đông) thì thấy hoàn toàn đúng — dư ảnh **thực sự** nằm
sau cạnh phẳng, đúng theo quy ước `-Y=mũi` đã dùng xuyên suốt file. Hỏi lại người dùng cụ thể "vệt nằm
ở đầu nhọn hay đầu phẳng" thì xác nhận: **đầu nhọn**. Vấn đề không phải toán mà là **hình dạng thân
tàu** — bản trước vẽ mũi bằng/đuôi nhọn (đúng theo spec gốc và đúng quy ước code), nhưng mắt người đọc
"đầu nhọn = phía trước" gần như bản năng (như mũi tên/đầu xe), nên khi thêm vệt kéo dài sau mũi bằng
(đúng), người xem lại đọc mũi bằng thành đuôi và đầu nhọn thành mũi — khiến vệt "trông như" ở phía
trước. Fix: **đổi hẳn hình dạng tàu thành mũi nhọn/đuôi phẳng** (`ShipLocalPoints()`, dùng chung cho
cả `DrawShip` và `DrawShipTrail` để không bao giờ lệch nhau) — khớp bản năng đọc hình, quy ước
`-Y=mũi` không đổi (chỉ đổi đầu nào trông nhọn/phẳng, không đổi đầu nào LÀ mũi về mặt toán) nên
`RotatePoint`/`DrawHeadingLine`/`bowY`/`sternY` không cần sửa gì. **`RadarControl.DrawShip` cũng đổi
theo y hệt** để 2 control không lại lệch hình dạng như lần trước (xem comment tại chỗ đó) — đây thực
ra là **quay lại đúng hình dạng gốc ban đầu** của `RadarControl` (nó vốn đã mũi nhọn/đuôi phẳng trước
khi bị đổi để khớp `ConningControl` lúc ConningControl còn mũi bằng).

**Hình tàu đổi từ tô đặc sang khung viền (2026-09-03)** — theo phản hồi "hình tàu để dạng khung line
thôi, để dạng sharp khó theo dõi": `DrawShip()` bỏ hẳn `g.FillPolygon(fill, pts)` (tô đặc màu cam
`ColShipF`), chỉ còn `g.DrawPolygon(bpen, pts)` (khung viền `ColShipB`) — thân tàu tô đặc che khuất
lưới ô ly/vệt track/hình target/mũi tên báo hiệu ngoài tầm ngay bên dưới nó, khó theo dõi các lớp vẽ
chồng lên nhau quanh khu vực trung tâm. Độ dày viền tăng 1.5f→2.2f để hình vẫn rõ dù không còn tô đặc
hỗ trợ. `ColShipF` (màu tô) vẫn còn dùng ở `DrawShipTrail` — dư ảnh vốn đã vẽ bằng `FillPolygon` với
alpha trong suốt (không phải tô đặc), không phải hình chính, không đổi theo yêu cầu này.

**Viền đậm hơn (2026-09-03)** — theo phản hồi "viền tàu hiện tại đậm hơn 1 chút", tăng tiếp độ dày
`bpen` từ `2.2f` lên `2.8f` (chỉ đổi độ dày nét, không đổi màu `ColShipB` hay bo góc).

**Hình tàu bo tròn góc (2026-09-03, ngay sau đổi khung viền)** — người dùng gửi ảnh chụp 1 conning
display thật (thanh turn-rate/heading dạng tape, khung hình tàu ở giữa bo tròn mượt mà, không góc
nhọn) kèm yêu cầu "vẽ hình tàu bo lại như thế này". `ShipLocalPoints()` (5 đỉnh: mũi nhọn, 2 vai,
2 góc đuôi) **giữ nguyên không đổi** — tỉ lệ thân tàu không đổi, chỉ đổi CÁCH VẼ nối các đỉnh đó.
- **Lần 1 (bị revert ngay) — `GraphicsPath.AddClosedCurve(pts, tension)`:** đường cong Catmull-Rom
  khép kín đi qua đúng 5 điểm cũ, tưởng sẽ tự động bo mọi góc mà không cần tính cung tròn thủ công.
  **Sai** — vì đây là spline đi qua TOÀN BỘ contour chứ không chỉ bo góc cục bộ, và 2 điểm "vai" vốn
  đã gần full-beam ngay sát mũi tàu khiến đường cong vọt rộng ra ngay sau mũi — kết quả nhìn như 1
  viên con nhộng/thuốc con nhộng đối xứng, mất hẳn dáng mũi thon của tàu. Người dùng phản hồi ngay
  "sao sửa thành hình con nhộng vậy" — không giống ảnh tham khảo (tàu vẫn thon dài, chỉ góc được bo).
- **Lần 2 (hiện tại) — bo góc cục bộ kiểu "rounded polygon", giữ nguyên cạnh thẳng:** thêm
  `BuildRoundedShipPath(pts, radius)` — mỗi đỉnh P[i] được thay bằng 2 điểm lùi vào `radius` dọc theo
  2 cạnh kề (kẹp lại còn tối đa nửa cạnh ngắn hơn, để cạnh đuôi hẹp — dài `2×sw` — không bị chồng
  lấn), nối 2 điểm đó bằng 1 đường Bézier bậc 2 cong qua ĐÚNG đỉnh gốc làm điểm điều khiển
  (`GraphicsPath` không có `AddQuadraticBezier` sẵn — quy đổi sang Bézier bậc 3 tương đương bằng công
  thức chuẩn `C1/C2 = điểm_đầu/cuối + 2/3×(điểm_điều_khiển − điểm_đầu/cuối)`). Cạnh thẳng giữa các
  góc giữ nguyên không đổi — chỉ 5 góc (mũi, 2 vai, 2 đuôi) được bo, giữ đúng dáng thon dài thật của
  tàu thay vì biến cả đường viền thành 1 spline lỏng lẻo. `radius = sw × 0.7` (tuỳ chỉnh được).
  Áp dụng cho cả `DrawShip` (khung viền, `g.DrawPath`) lẫn `DrawShipTrail` (dư ảnh tô mờ,
  `g.FillPath`) để tàu thật và dư ảnh luôn cùng 1 hình dạng. Hàm build path chạy TRÊN CÁC ĐIỂM ĐÃ
  XOAY (`RotatePoint` áp dụng trước khi gọi) vẫn cho kết quả đúng vì phép nội/ngoại suy tuyến tính +
  Bézier đều giao hoán được với phép xoay affine — không cần build path ở hệ toạ độ local rồi xoay cả
  `GraphicsPath` bằng `Matrix`. `LineJoin.Round` thêm vào `Pen` của `DrawShip` cho khớp nét vẽ mượt.

**Tỉ lệ dài/rộng hình tàu đổi thành 4:2, WIDTH không còn theo LOB thật (2026-09-03, ngay sau bo góc,
chỉnh lại thành 4:2 + góc bo nhọn hơn cùng ngày)** — người dùng yêu cầu "giảm chiều dài tăng chiều
rộng tỉ lệ 2 3". Trước đây `sw` (nửa chiều rộng vẽ) tính thẳng từ `LOB` thật × `pxPerMeter` — với LOB
mặc định 20m so với LOA 100m (tỉ lệ thật ~5:1), hình tàu vẽ ra là 1 sợi mảnh rất khó đọc heading/hướng
quay, và không giống dáng mập/ngắn của icon tàu trong ảnh conning display tham khảo. Qua
`AskUserQuestion` làm rõ "2:3" là Dài:Rộng hay Rộng:Dài, người dùng chọn ban đầu **Dài:Rộng = 3:2**
(tàu vẫn dài hơn rộng, không lật ngược thành rộng hơn dài) — sau đó điều chỉnh tiếp sang **4:2** (=
2:1, thon hơn 1 chút so với 3:2). Fix: `sw = sl / (ShipLengthToWidthRatio)` với
`ShipLengthToWidthRatio = 4f/2f` — `sw` là 1 tỉ lệ cố định của `sl` (đã tính true-scale theo
LOA/RANGE như cũ), **không còn phụ thuộc `LOB` thật nữa** — đây là icon cách điệu (stylized), không
phải beam thật của tàu. `sl`/chiều dài **vẫn giữ nguyên true-scale theo LOA/RANGE** như thiết kế "gộp
ZOOM vào RANGE" trước đó — chỉ WIDTH đổi cách tính. `_lob`/tham số `lob` trong `Update()` vẫn còn nhận
từ `MainForm` (không đổi API công khai) nhưng không còn dùng để vẽ hình tàu nữa.

**Góc bo nhọn lại (cùng ngày, ngay sau đổi tỉ lệ 4:2)** — người dùng phản hồi tiếp "cho các góc bo
nhọn lại" (bán kính bo góc `sw × 0.7` ở mục "Hình tàu bo tròn góc" trông quá tròn/mềm). Thêm hằng số
`ShipCornerRadiusFactor = 0.35f` (giảm từ 0.7 xuống một nửa) dùng chung cho cả 2 chỗ gọi
`BuildRoundedShipPath` (`DrawShip` và `DrawShipTrail`) — góc vẫn mềm hơn polygon gốc (không sắc như
dao) nhưng không còn tròn/phồng như trước. Bán kính vẫn tự động scale theo `sw` mới (nay đã nhỏ hơn
do tỉ lệ 4:2 thay vì 3:2), không cần chỉnh gì thêm ở nơi khác.

### Giá trị số trên mũi tên
`DrawValueLabel()` — không khung/nền, chỉ vẽ số (halo viền màu nền `Palette.RadarFace` quanh glyph để
đọc được dù đè lên ring/tàu), cỡ chữ `Clamp(R×0.13, 11, 22) × zoom`. Không có nhãn tên, không đơn vị,
không chữ hướng (STBD/PORT/FWD/AFT/REL) — hướng đã thể hiện qua hướng vẽ mũi tên.

**Co giãn theo ZOOM (2026-08-28) → gộp vào RANGE rồi xoá (2026-09-03) → thêm lại tự động theo RANGE,
kẹp trần/sàn (2026-09-03, cùng ngày):** mũi tên lateral/axial + vòng heave + `DrawValueLabel` từng co
giãn theo 1 tham số `zoom` lấy từ slider ZOOM độc lập (nhân vào độ dày `Pen` và cỡ chữ). Khi ZOOM được
gộp vào RANGE, tham số `zoom` này bị xoá hẳn (độ dày/cỡ chữ thành hằng số cố định) — nhưng người dùng
sau đó yêu cầu "các mũi tên và số cũng thu phóng theo luôn, đến 1 mức to hoặc nhỏ nhất định để dễ
nhìn", nên tham số `zoom` được **thêm lại**, lần này lấy từ `visualZoom` tính tự động trong `OnPaint`
(`Clamp(sl / (R×0.40), 0.6, 1.8)` — so `sl` hiện tại, đã theo tỉ lệ thật của RANGE, với mốc cố định cũ
`R×0.40`) thay vì 1 slider thủ công riêng. `arrowScale`/`arrowMax` (độ dài mũi tên) cũng nhân
`visualZoom`. Khoảng kẹp `[0.6, 1.8]` chính là "mức to/nhỏ nhất định" — mũi tên/chữ số phóng to khi
RANGE nhỏ (tàu to ra) và thu nhỏ khi RANGE lớn (tàu co lại), nhưng không bao giờ vượt quá biên đó dù
`sl` thực tế biến thiên hàng chục/hàng trăm lần giữa 50m và 10km. `DrawWindArrow` không đổi gì (vốn dĩ
neo theo true bearing trên ring cố định, không gắn theo tàu, luôn dùng `zoom` mặc định `1f`).

**Trần kẹp hạ 1.8→1.2 (2026-09-03, ngay sau khi thêm thang RANGE 5/10/20m)** — thêm 3 mức RANGE cận
mới (5m/10m/20m, xem mục RANGE bên dưới) khiến `sl` (tàu LOA~100m vẽ ở RANGE chỉ vài mét) lớn hơn mốc
`R×0.40` tới hàng chục lần — `visualZoom` **luôn kẹp trần** ở cả 3 mức RANGE mới này (không riêng gì
lúc "RANGE=50m" như comment gốc dự tính khi trần còn là 1.8). Người dùng phản hồi "5 10 20 m khi zoom
thì các mũi tên hiển thị bị quá to" — hạ `VisualZoomMax` từ `1.8` xuống `1.2`: mũi tên/chữ số vẫn to
lên rõ rệt khi zoom cận, nhưng không còn phóng đại mạnh như trước. `VisualZoomMin=0.6` giữ nguyên
(không liên quan — chỉ ảnh hưởng đầu RANGE lớn/1000m).

**Bug "mũi tên biến mất hoàn toàn từ RANGE=20m trở xuống" (2026-09-03, ngay sau fix trần 1.2× ở
trên) — `sl` chỉ có sàn, chưa có trần:** hạ trần `visualZoom` (ở trên) chỉ chặn độ dày nét/cỡ chữ
phồng to — không chặn được chính `sl` (nửa chiều dài tàu vẽ, theo tỉ lệ thật LOA/RANGE) tăng vô hạn.
Ở RANGE=20m/10m/5m, `sl` tính ra lớn hơn bán kính ring `R` tới hàng chục lần — kéo theo 2 hậu quả:
(1) `bowY`/`sternY` (điểm neo mũi tên lateral, = `-sl×0.72`/`sl×0.36`) văng ra ngoài hẳn vùng nhìn
thấy của control, không phải "mũi tên nhỏ xíu" mà là **vẽ ở toạ độ hoàn toàn ngoài canvas**; (2) vòng
tròn heave (`DrawHeaveVector`, bán kính `sw×0.55`, `sw` tỉ lệ thuận với `sl`) phồng theo thành 1 khối
tròn tô đặc khổng lồ — do vẽ **sau cùng** trong nhóm phần tử gắn theo tàu (`OnPaint`: mũi tên → vòng
heave), khối tròn này đè kín luôn cả mũi tên axial (vốn vẫn xuất phát đúng tâm `(cx,cy)` nên lẽ ra
không văng ra ngoài). Người dùng xác nhận qua ảnh chụp 4 mức RANGE (50m/20m/10m/5m): "bắt đầu từ 20m
là các mũi tên không thể nhìn thấy nữa". **Fix:** thêm **trần** cho `sl` — `Math.Clamp(...,
ShipMinHalfLenPx, R × ShipMaxHalfLenFactor)` với `ShipMaxHalfLenFactor = 0.85` — `sl` (và do đó `sw`,
`bowY`/`sternY`, bán kính vòng heave — tất cả đều suy ra từ `sl`) giờ **không bao giờ vượt quá 85%
bán kính ring**, dù LOA/RANGE tính ra tỉ lệ thật lớn hơn bao nhiêu. Đánh đổi: tàu ở RANGE cực nhỏ
(5-20m, mặc định LOA=100m) không còn vẽ đúng 100% tỉ lệ thật nữa (bị kẹp lại ở trần) — nhưng đổi lại
mũi tên/vòng heave **luôn nằm trong vùng nhìn thấy ở mọi RANGE**, đúng yêu cầu "vẫn có thể nhìn thấy
tất cả mũi tên ở tất cả khoảng cách". Với RANGE≥~55m (LOA mặc định 100m), `sl` tính ra tự nhiên đã
dưới trần nên hành vi true-scale cũ không đổi — trần chỉ thực sự có tác dụng ở đúng dải RANGE cận mới
thêm (5/10/20/50m) từng gây ra lỗi này.

**Hạ trần 0.85→0.40 (2026-09-03, ngay sau fix trần ở trên)** — trần 0.85 đã hết lỗi văng-ra-ngoài,
nhưng tàu vẫn chiếm gần hết ring ở RANGE=50m trở xuống — người dùng gửi ảnh chụp RANGE=50m kèm phản
hồi "50m để nhỏ lại vừa đủ k quá to như hiện tại, các kích thước kia đồng thời cũng tinh chỉnh". Với
LOA mặc định 100m, `sl` thô (chưa kẹp) đã vượt quá **cả** 0.85×R lẫn 0.40×R ở mọi mức RANGE từ 5m đến
100m — nên chỉ cần hạ `ShipMaxHalfLenFactor` xuống `0.40` là tàu tự động thu nhỏ đồng loạt ở **cả 5
mức RANGE cận** (5/10/20/50/100m, không chỉ riêng 50m) trong 1 lần đổi — đúng ý "các kích thước kia
đồng thời cũng tinh chỉnh". Giá trị `0.40` không phải chọn tuỳ ý — đây chính là hệ số `R×0.40` đã
dùng làm mốc tham chiếu cho `visualZoom` ngay bên dưới (kích thước tàu cố định trước khi RANGE điều
khiển kích thước tàu), nên khi tàu bị kẹp ở trần này, `visualZoom` tự nhiên ra đúng `1.0` (không phóng
to/nhỏ gì thêm) — 2 con số ăn khớp nhau thay vì lệch pha như trước. Chỉ RANGE=1000m còn giữ true-scale
thật (dưới trần, tàu nhỏ gần sàn).

**Trần theo bậc RANGE thay vì 1 hằng số (2026-09-03, ngay sau đổi 0.85→0.40)** — người dùng yêu cầu
tiếp "100m nhỏ bằng 1 nửa hiện tại, 50m trở xuống thì giữ nguyên". Vì 5/10/20/50/100m trước đó đều
dùng CHUNG 1 trần `0.40` (nên tàu vẽ giống hệt nhau ở cả 5 mức — xem mục ngay trên), không có cách
nào chỉnh riêng 100m mà không đụng tới 50m trở xuống nếu còn dùng 1 hằng số duy nhất. Đổi
`ShipMaxHalfLenFactor` từ `const float` sang biến tính theo `RangeMeters`:
`shipMaxHalfLenFactor = RangeMeters <= 50 ? 0.40f : 0.20f` — RANGE≤50m giữ nguyên trần cũ (`0.40`),
RANGE≥100m dùng trần bằng **đúng 1 nửa** (`0.20`). Không cần sửa gì thêm ở nơi khác: `sw`/`bowY`/
`sternY`/bán kính vòng heave đều suy ra từ `sl` nên tự động thu nhỏ theo; `visualZoom` (mốc tham
chiếu vẫn cố định `R×0.40`, không đổi theo bậc) ở RANGE=100m giờ tính ra `sl/(R×0.40) = 0.20/0.40 =
0.5`, bị kẹp lên sàn `VisualZoomMin=0.6` — mũi tên/chữ số ở 100m nhỏ hơn 1 chút so với 50m trở xuống,
hợp lý vì bản thân tàu cũng nhỏ hơn. RANGE=1000m không đổi (vẫn true-scale tự nhiên, nằm dưới cả 2
mức trần).

**Thêm mốc 500m, đổi từ 300m (2026-09-03, ngay sau fix trần theo bậc)** — theo yêu cầu "thêm mốc
300m", chèn giữa 100m và 1000m: `RangePresetsM = {5,10,20,50,100,300,1000}` (7 mức, trước là 6 — bước
nhảy 100m→1000m cũ là 10× khá xa, giờ có thêm nấc trung gian). Ngay sau đó người dùng yêu cầu "thay
300m bằng 500m" — đổi thành `{5,10,20,50,100,500,1000}`. `_rangeIndex` mặc định vẫn `3` (50m) — không
đổi ở cả 2 lần, vì mốc chèn/đổi đều nằm SAU vị trí index 3 nên không làm lệch preset mặc định. Trần
theo bậc (mục ngay trên) vẫn đúng logic mà không cần sửa: điều kiện `RangeMeters <= 50` dùng so sánh
giá trị thật (không phải theo vị trí mảng), nên mốc mới (500m) tự động rơi vào nhánh trần `0.20` giống
100m/1000m, không cần thêm case riêng.

### Vị trí giá trị
- **Lateral (BOW/STN):** ngoài đầu mũi tên (offset R×0.06 theo trục ngang local), xoay theo hdg cùng mũi tên
- **Axial (LONG):** ngoài đầu mũi tên theo trục dọc local (offset R×0.05), xoay theo hdg cùng mũi tên
- **Wind:** lệch vuông góc với hướng gió tại điểm trong của mũi tên (không xoay theo hdg — ring cố định nên
  mũi tên wind dùng true bearing trực tiếp)

---

## DUO GPS mode (2 nguồn GPS dự phòng)

Bật/tắt: `SystemConfig.DuoGpsEnabled` (ConfigForm ▸ COM Config ▸ checkbox "Enable DUO GPS mode").
Đổi COM port/baud cho task `GPS2` xong cần **restart app** để áp dụng (giống mọi thay đổi COM port khác).

**Kiến trúc:** `NmeaParserService` không còn biết gì về "GPS1 vs GPS2" — mọi event liên quan vị trí đều mang
theo `portName` gốc (`OnPositionParsed`, `OnPositionRawParsed`, `OnSpeedParsed`, `OnCogParsed`,
`OnGpsQualityParsed`). `MainForm` là nơi duy nhất biết mapping port→nguồn:

- `GpsSourceForPort(port)` — tra `ConfigForm.Tasks` theo `PortName`, trả về `_gps1` (TaskName="GPS") hoặc
  `_gps2` (TaskName="GPS2"), mỗi cái là 1 `GpsSource` (lat/lon str + raw deg, speed, quality, LastUpdate).
- `SelectActiveGpsSource()` — chạy mỗi khi 1 nguồn báo quality mới. Nếu DUO tắt → luôn dùng nguồn 1.
  Nếu 1 nguồn mất fix (`HasFix` = có toạ độ hợp lệ + cập nhật trong <5s) → dùng nguồn còn lại. Nếu cả 2 còn
  fix → so `QualityRank()` (RTK FIX=4 > RTK FLOAT=3 > DGPS=2 > GPS/PPS=1 > estimated/manual/sim=-1), **bằng
  nhau thì giữ nguyên nguồn đang dùng** (sticky, tránh nhảy qua lại).
- `PublishActiveGps()` — đẩy DUY NHẤT dữ liệu của nguồn đang active vào `ConningDataHub` (qua
  `UpdateGpsData`/`UpdateGpsRaw`/`UpdateGpsFixQuality`); phần còn lại của app (POSITION card, ConningControl,
  Targets, Position Watch) hoàn toàn không biết có DUO mode.
- Badge GPS ở top bar khi DUO bật hiện thêm số nguồn: `GPS: DGPS·1` / `GPS: RTK FIX·2`.
- Alarm `AL_GPSDUO` (Tag `GpsDuoDivergence`, limit `SystemConfig.GpsDuoDivergenceM`, mặc định 50m) — tính
  khoảng cách Haversine giữa 2 nguồn mỗi tick khi cả 2 đều có fix; raise khi lệch quá ngưỡng.

**Đã tắt (2026-09-07), code vẫn còn nguyên:** theo yêu cầu người dùng ("bỏ GPS2, dùng 1 GPS, nhưng
comment lại chứ không xoá, phòng dùng lại sau"), toàn bộ kiến trúc mô tả ở trên (task `GPS2`,
`_gps2`/`_activeGpsSource`, `SelectActiveGpsSource`/`QualityRank`, `AL_GPSDUO`/`_gpsDuoTag`, checkbox
"Enable DUO GPS mode" + divergence numeric trong ConfigForm ▸ COM Config, khối GPS2 trong
`SimulationEngine`, 2 property `DuoGpsEnabled`/`GpsDuoDivergenceM` trong `AppConfig`/`SystemConfig`) đã
bị bọc trong `#if DUO_GPS_ENABLED ... #endif` — **không xoá**, chỉ compile-out. App hiện tại chỉ có 1
nguồn GPS (`_gps1`, task `"GPS"`, COM1 mặc định); `GpsSourceForPort`/`ActiveGpsSource` vẫn còn (đơn giản
hoá còn luôn trả `_gps1`) để `OnPositionParsed`/`OnCogParsed`/... không cần sửa gì thêm. Muốn bật lại:
thêm `<DefineConstants>DUO_GPS_ENABLED</DefineConstants>` vào `ConningMonitorPRS.csproj`, không cần sửa
code nào khác. `config.json` cũ còn field `DuoGpsEnabled`/`GpsDuoDivergenceM`/task `"GPS2"` vẫn load
được bình thường (System.Text.Json bỏ qua property lạ, vòng lặp merge task no-op khi không khớp tên).

---

## Position Watch & Targets

**Position Watch (drift-off alarm):** `SystemConfig.DriftWatchEnabled/DriftRefLat/DriftRefLon/DriftRadiusM`.
Cấu hình ở ConfigForm ▸ tab **Position Watch** (đổi tên từ "Targets & Watch" 2026-09-03, xem mục
"Targets dời hẳn khỏi Settings" bên dưới — tab giờ **chỉ** có Position Watch, có nút "Use Current GPS
Position" tự điền toạ độ hiện tại). `MainForm.UiTick` tính `GeoMath.DistanceMeters(RefLat,RefLon,
snap.GpsLatDeg,snap.GpsLonDeg)` mỗi 100ms → Tag `DriftDistance` → Alarm `AL_DRIFT` (limit = `DriftRadiusM`).

**Targets:** tối đa **300** `TargetPoint` (Name/Lat/Lon/Enabled, tăng từ 4 — 2026-09-03) trong
`SystemConfig.Targets`. Một target chỉ tính là `Enabled` khi tick "On" **và** cả Lat lẫn Lon parse được
số thực lúc Save. **Sửa được ở đúng 1 nơi duy nhất — `TargetsForm`** (nút 🎯 TARGETS, không yêu cầu
login) — có **grid sửa riêng** (`_dgvEdit`, `TargetsForm.SaveEditGrid`) + nút "SAVE TARGETS", tách biệt
hoàn toàn khỏi grid hiển thị sống bearing/distance ở trên (grid đó `Rows.Clear()`/`Add()` lại mỗi 500ms
nên không thể vừa live-refresh vừa cho sửa tay được — 2 grid riêng, grid sửa chỉ load 1 lần lúc mở
form, không bị tick ghi đè).

**Targets dời hẳn khỏi Settings (2026-09-03)** — trước đây targets sửa được ở **2 nơi** (`ConfigForm`
▸ tab "Targets & Watch", `_dgvTargets`, cần login admin — **và** `TargetsForm`, không cần login),
đồng bộ qua cùng `SystemConfig.Targets`/`config.json` nhưng **không tự sync giữa 2 form đang mở cùng
lúc** (sửa ở form này rồi mở form kia không thấy ngay, phải đóng mở lại). Theo yêu cầu "bỏ set target
trong setting đi", `ConfigForm.SetupTargetsTab` (đổi tên `SetupPositionWatchTab`) bỏ hẳn `_dgvTargets`
+ nhãn "TARGETS (up to 4)..." — `BtnSave_Click` không còn đụng tới `SystemConfig.Targets` nữa (để
nguyên bất kể `TargetsForm` đã lưu gì trước đó). Tab đổi tên **"Targets & Watch" → "Position Watch"**
(chỉ còn Position Watch). Loại bỏ hẳn vấn đề đồng bộ 2 nơi — `TargetsForm` giờ là **nguồn chỉnh sửa
target duy nhất**, không cần login (targets vẫn là waypoint vận hành, không phải cấu hình nhạy cảm).

**Tăng giới hạn 4→300 target, 4→6 toạ độ/target + thêm dòng động (2026-09-03)** — theo yêu cầu "Số
lượng target nên để khoảng 300 / mỗi target 6 tọa độ / ... cách thêm thì mới đầu để như hiện tại 4 cái,
muốn thêm thì thêm bằng nút thêm sẽ thêm xuống dưới / list có scroll down ở trong targetform và
mainform luôn":
- `TargetsForm` có `MaxTargets=300`, `MaxCoords=6` (đỉnh chính + tối đa 5 `ExtraPoints`, tăng từ 3),
  `InitialTargetRows=4` (vẫn mở form ra thấy 4 dòng trống như cũ). `_dgvEdit` mở rộng cột
  `Lat2/Lon2`…`Lat6/Lon6` (5 cặp thay vì 3). `AddTargetRow(name?)` (helper mới) chèn 1 dòng trống ở
  **cuối** grid — dùng cho cả lần khởi tạo 4 dòng đầu lẫn nút **"+ ADD TARGET"** mới cạnh "SAVE
  TARGETS" (no-op im lặng nếu đã chạm `MaxTargets`, không cần báo lỗi vì 300 đủ rộng để việc chạm trần
  tự nó là dấu hiệu bất thường). `LoadEditGrid()` giờ tự **mở rộng số dòng** cho khớp
  `SystemConfig.Targets.Count` đã lưu trước đó (không còn cứng 4) trước khi đổ dữ liệu vào.
- `_dgvEdit` đổi `AutoSizeColumnsMode` từ `Fill` sang `None` + `Width` cố định từng cột — với 14 cột
  (On/Name/Lat/Lon + 5×Lat/Lon) `Fill` sẽ ép hết vào bề ngang cửa sổ, cột Lat/Lon nhỏ tới mức không
  gõ được; `None` giữ mỗi cột đủ rộng để đọc, đổi lại **grid giờ cuộn ngang** khi cửa sổ hẹp hơn tổng
  bề ngang cột (`DataGridView` tự cuộn ngang mặc định, không cần code thêm).
- **"list có scroll down"** — cả `_dgv` (grid xem sống) lẫn `_dgvEdit` (grid sửa) trong `TargetsForm`
  **đều đã là `DataGridView`**, vốn dĩ tự cuộn dọc khi số dòng vượt vùng hiển thị — không cần thêm
  code cho `TargetsForm`. Mini Targets card trong `MainForm` thì khác — xem mục ngay dưới.
- **`MainForm`'s mini Targets card** (`_miniTargetRows`) trước là **mảng 4 phần tử cố định**
  (`(Panel,Label,Label,Label)[4]`), dựng sẵn hết trong `BuildMiniTargetsCard` — không scale được lên
  300. Đổi thành **`List<...>` tự phình to** (`EnsureMiniTargetRow(index)` dựng thêm 1 dòng nếu chưa
  có, `RefreshMiniTargets` gọi hàm này cho mỗi target `Enabled` thay vì cắt cứng ở `shown >=
  _miniTargetRows.Length`) — chỉ dựng đúng số dòng thật sự cần (target đang bật), không dựng sẵn cả
  300 dòng lúc khởi động. `listPanel` (đổi tên field `_miniTargetsListPanel`) thêm `AutoScroll = true`
  để cuộn dọc khi nhiều target hơn chiều cao card cho phép.
  - **Thứ tự dòng khi phình động** — logic cũ ("row 0 add cuối cùng để nổi lên trên") chỉ tính trước
    cho ĐÚNG 4 dòng biết trước; với dòng thêm dần không biết trước tổng số, `EnsureMiniTargetRow` dùng
    `Controls.Add(row); row.BringToFront();` — `BringToFront()` đưa dòng mới về index 0 trong
    `Controls`, khiến nó dock **CUỐI CÙNG** trong lượt xử lý Dock=Top (WinForms xử lý theo thứ tự
    index ngược, index cao dock trước/nổi trên) → luôn rơi xuống **đáy** của chồng dòng đã có, đúng vị
    trí 1 dòng mới cần. `colHeader` (dựng 1 lần, không đụng lại) luôn giữ index cao nhất nên luôn nổi
    trên cùng bất kể phình thêm bao nhiêu dòng.
- `ConningControl.DrawOffRangeTargetIndicators`'s thuật toán gom nhóm nhãn O(n²) (xem mục "Xếp chồng
  nhãn khi nhiều target cùng hướng") vẫn đủ nhanh ở 300 target (~90 nghìn phép so sánh double/lần vẽ,
  không đáng kể so với chi phí vẽ GDI+ mỗi khung hình) — không cần tối ưu lại thuật toán.

**Nút xoá từng dòng + target mới mặc định lấy GPS hiện tại (2026-09-03)** — theo yêu cầu "có nút thêm
nút xóa target ở mỗi target, target khi add mặc định sẽ lấy tọa độ hiện tại":
- Thêm cột `DataGridViewButtonColumn` tên `"Delete"` (chữ `✕`, `UseColumnTextForButtonValue=true` nên
  không cần set `Value` riêng từng ô) làm cột **đầu tiên** trong `_dgvEdit` — đặt bên trái nhất để luôn
  thấy được mà không cần cuộn ngang qua hết 14 cột toạ độ. `CellContentClick` bắt click vào đúng cột
  này (`e.RowIndex >= 0` để loại click vào header) rồi `_dgvEdit.Rows.RemoveAt(e.RowIndex)` — xoá ngay
  lập tức, không hỏi xác nhận (giống triết lý "targets là waypoint vận hành, không phải cấu hình nhạy
  cảm" đã áp dụng cho việc không cần login sửa target). Màu chữ nút dùng `Palette.AlarmActiveFg` (màu
  cảnh báo đỏ đã dùng cho trạng thái vượt Position Watch) để gợi ý đây là hành động xoá.
- `AddTargetRow(name?, lat?, lon?)` thêm 2 tham số tuỳ chọn `lat`/`lon` — nút **"+ ADD TARGET"** giờ
  đọc `ConningDataHub.Instance.GetSnapshot()` (cùng nguồn `ConfigForm`'s nút "Use Current GPS
  Position" dùng cho Position Watch) và truyền toạ độ hiện tại vào nếu có fix; không có fix thì rơi về
  hành vi cũ (để trống, không có popup chặn). 4 dòng khởi tạo ban đầu (`InitialTargetRows`) và dòng
  `LoadEditGrid()` tự thêm khi mở rộng grid **không đổi** — vẫn để trống như cũ, chỉ dòng thêm qua nút
  bấm mới tự điền GPS.
- Vì thêm cột `Delete` ở vị trí đầu, mảng giá trị truyền vào `_dgvEdit.Rows.Add(vals.ToArray())` trong
  `AddTargetRow` phải thêm 1 phần tử placeholder (`""`) ở đầu cho khớp thứ tự cột — `LoadEditGrid`/
  `SaveEditGrid` không cần sửa vì cả 2 đều truy cập cell theo TÊN cột (`row.Cells["Enabled"]` v.v.),
  không phụ thuộc vị trí.

**Target dạng hình (2026-08-28, mở rộng 2026-09-03)** — `TargetPoint` thêm `ExtraPoints`
(`List<LatLon>`, tối đa **5** tọa độ nữa ngoài `Lat`/`Lon` chính — tăng từ 3, mặc định rỗng nên
`config.json` cũ tự động load thành target điểm đơn, không cần migrate). Lúc Save, 1 đỉnh chỉ được
thêm vào `ExtraPoints` nếu **cả 2** ô Lat/Lon của đỉnh đó parse được số hợp lệ, thiếu 1 trong 2 thì
coi như chưa nhập (không báo lỗi, không chặn Save). Được `ConningControl.DrawTargets()` vẽ thành hình
trên radar chính (1 đỉnh = chấm, 2 đỉnh = đoạn thẳng, ≥3 đỉnh = đa giác), nhưng bearing/distance hiển
thị ở `TargetsForm`/mini card **vẫn chỉ tính tới đỉnh chính** (`Lat`/`Lon`) — không tính centroid hay
cạnh gần nhất cho target dạng hình, giữ đơn giản theo đúng yêu cầu ban đầu.

Grid bearing/distance sống tính lại mỗi 500ms từ snapshot hiện tại — không có gì được cache/lưu lịch sử.

---

## TRENDS window (trước đây "Radar/Trends" — radar đã bị gộp hẳn vào ConningControl, 2026-08-28)

Nút **TRENDS** mở `TrendsForm` — giờ **chỉ còn `TrendChartControl`** (trong `Panel` + toolbar
`FlowLayoutPanel` có `AutoScroll=true`), dock `Fill` thẳng vào Form. Trước đó cửa sổ này còn có
`RadarControl` (trái) trong 1 `SplitContainer`, nhưng theo yêu cầu người dùng ("bỏ luôn radar trong
trendform đi, tích hợp hết vô radar ở mainform") — **`RadarControl.cs` bị xoá hoàn toàn khỏi codebase**,
toàn bộ vai trò của nó (RANGE control, 30-phút own-ship track, và tính năng mới vẽ target dạng hình —
xem 2 mục ngay dưới) được port thẳng vào `ConningControl` (radar chính giữa MainForm). Bỏ luôn
`SplitContainer` khỏi `TrendsForm` — không còn cần set `SplitterDistance`/`Panel1MinSize`/
`Panel2MinSize` trong `Form.Load` ở đây nữa (gotcha "⚠️ Gotcha của SplitContainer" bên dưới giờ chỉ
còn áp dụng cho `ConfigForm`'s Alarm History tab, `SetupHistoryTab`).

**RANGE + own-ship track (trong `ConningControl`, 2026-08-28)** — port gần như nguyên văn từ
`RadarControl` (đã xoá). Góc **dưới-trái** — thang đo khoảng cách thật (`RangePresetsM =
{50,100,250,500,1000,2000,5000,10000}` mét, nút `+`/`−` rời rạc — không phải slider vì RANGE trải
200×, không hợp thanh trượt tuyến tính). **Kể từ 2026-09-03 (gộp ZOOM vào RANGE, xem mục "Gộp ZOOM
vào RANGE" trong "Lưới ô ly" ở phần ConningControl bên dưới), RANGE là thang đo mét→pixel DUY NHẤT
cho toàn bộ control** — không còn ZOOM tách biệt nữa. `PushTrackPoint(lat,lon)` (gọi từ `MainForm.
UiTick` mỗi tick có fix GPS) tích luỹ buffer 30 phút; `DrawTrack()` chiếu các điểm cũ sang mét bằng
`GeoMath.OffsetMeters()` (helper trong `Core/Geo/GeoMath.cs`, gộp công thức xấp xỉ equirectangular
từng bị viết tay lặp lại ở `RadarControl.DrawTrack` và `ConningControl.DrawShipTrail`) và scale theo
`RangeMeters` — **vệt vẽ tương đối so với vị trí hiện tại**, không phải toạ độ tuyệt đối trên màn
hình; điểm nằm ngoài range đã chọn thì vẽ tràn ra ngoài ring (không clip). Vòng lưới tròn (`grid
circles` trong `DrawCompassRing`) hiện nhãn khoảng cách gắn với `RangeMeters` thật — **cùng thang đo
với lưới ô vuông graph-paper** (trước 2026-09-03 lưới ô vuông dùng thang LOA/ZOOM riêng, khác với
RANGE; nay cả 2 dùng chung `pxPerMeter` phái sinh từ `RangeMeters`, xem mục "Gộp ZOOM vào RANGE").

**Bug "vòng RANGE và target/track lệch thang đo" + làm rõ số RANGE (2026-09-03, theo yêu cầu người
dùng "kiểm tra xem khoảng cách tính của range và các target hiện trên radar đã chuẩn tính toán
chưa"):** rà lại toàn bộ công thức chiếu toạ độ (`GeoMath.DistanceMeters`/`BearingDeg`/`OffsetMeters`,
`DrawTrack`, `DrawTargets`) — Haversine/bearing/equirectangular-offset và quy ước dấu Bắc=-Y/Đông=+X
đều đúng. Nhưng phát hiện 1 lệch thang đo thật: `DrawTrack`/`DrawTargets`/hình học con tàu quy đổi
mét→pixel bằng `(R×0.94)/RangeMeters` (hệ số 0.94 chừa margin để tàu/mũi tên không chạm viền ring),
còn 3 vòng tròn khoảng cách có nhãn trong `DrawCompassRing` lại vẽ ở bán kính **`R×i/4` trần (không
nhân 0.94)** — lệch nhau đúng 6%: 1 target/track point nằm CHÍNH XÁC ở 75% RANGE đang chọn thực ra vẽ
ở pixel-radius 0.705R, trong khi vòng nhãn "75%" lại vẽ ở 0.75R — vòng nhãn và vị trí target/track
thật không khớp nhau dù cùng biểu diễn 1 khoảng cách. **Fix:** nhân thêm `0.94f` vào bán kính vòng
tròn (`ri = R × 0.94 × i / 4`) để dùng đúng 1 thang đo chung với `DrawTrack`/`DrawTargets`/tàu — giờ
1 target/track point ở đúng RANGE đang chọn sẽ nằm đúng lên viền ring ngoài cùng (94% bán kính, không
còn lệch 6%). Nhân tiện làm rõ số hơn theo yêu cầu: nhãn khoảng cách trên 3 vòng tròn đổi từ
`Segoe UI 6.5pt Regular` màu `Palette.RadarGrid` (màu lưới mờ, dễ chìm vào nền ô ly) sang
`8.5pt Bold` màu `Palette.RadarText` (cùng màu số N/E/S/W/30° đã dùng, tương phản cao hơn hẳn) +
halo nền `Palette.RadarFace` quanh chữ (`DrawOutlinedString`, cùng kỹ thuật `DrawValueLabel` đã dùng
cho số trên mũi tên) để đọc được dù đè lên lưới ô vuông/vệt track phía sau.

**Báo hiệu target ngoài tầm RANGE (2026-09-03)** — trước đây `DrawTargets` "vẽ tràn ra ngoài ring,
không clip" khi target xa hơn RANGE đang chọn — nhưng thực tế không phải "tràn ra ngoài ring" theo
nghĩa vẫn nhìn thấy được: điểm chiếu có thể rơi ra ngoài **toàn bộ control**, tức hoàn toàn không vẽ
gì cả — người dùng không có cách nào biết target đó tồn tại hay đang ở hướng nào cho tới khi bấm
RANGE `−` đủ nhiều lần. Theo yêu cầu "khi target bị zoom khỏi tầm thì hãy thêm 1 báo hiệu ở viền map
để người dùng biết target ở hướng nào", `DrawTargets` giờ tính khoảng cách thật (`GeoMath.
OffsetMeters` → Pythagoras) từ tàu tới **đỉnh chính** (`t.Lat`/`t.Lon`) mỗi target trước khi vẽ: nếu
`distM > RangeMeters` thì **bỏ qua vẽ hình** (chấm/đoạn/đa giác — vì điểm neo đã ở ngoài tầm nhìn),
thay bằng 1 **mũi tên chevron ngắn chĩa ra ngoài ngay tại viền ring**, đúng góc bearing thật của
target (`Math.Atan2(dxEast, dyNorth)`, cùng quy ước 0°=Bắc-clockwise với `DrawWindArrow`), kèm nhãn
"Tên + khoảng cách" (VD "Target 2 1.2km") đặt ngay phía trong mũi tên — cùng kiểu "off-screen
indicator" của game/phần mềm hàng hải khi có đối tượng ngoài khung nhìn. Chỉ đỉnh chính quyết định
trong/ngoài tầm (giữ đúng quy tắc "chỉ tính đỉnh chính cho mọi thứ liên quan bearing/distance" đã áp
dụng xuyên suốt Targets) — 1 target dạng hình có đỉnh chính còn trong tầm vẫn vẽ hình bình thường dù
1 góc xa hơn tràn ra ngoài ring (không đổi, không clip); chỉ khi chính đỉnh neo cũng vượt RANGE mới
chuyển hẳn sang mũi tên báo hiệu.

**Xếp chồng nhãn khi nhiều target cùng hướng (2026-09-03, ngay sau tính năng trên)** — với ≥2 target
ngoài tầm RANGE cùng nằm gần 1 hướng (VD cả 2 đều ở hướng Đông), bản đầu vẽ mọi nhãn ở đúng 1 khoảng
cách cố định tính từ viền ring → chữ đè lên nhau thành 1 vệt không đọc được. Theo phản hồi "chữ báo
target đó đừng để trùng nhau, mũi tên có thể trùng, nếu cùng 1 hướng thì để chữ 1 cái trên 1 cái
dưới" — `DrawTargets` tách thành 2 phần: vòng lặp vẽ hình target còn trong tầm (không đổi) +
`DrawOffRangeTargetIndicators()` mới lo riêng phần chevron/nhãn ngoài tầm. Hàm mới này:
1. Gom hết target ngoài tầm vào 1 danh sách kèm bearing thật, sắp xếp theo bearing.
2. Với mỗi target, đếm số target **đứng trước nó trong danh sách đã sắp** có bearing lệch dưới
   `GroupThresholdDeg=12°` (khoảng cách góc kiểu vòng tròn, xử lý đúng wraparound 359°↔1°) — số đếm
   này là `stack` (thứ hạng xếp chồng).
3. **Mũi tên chevron vẫn vẽ ở đúng bearing thật của từng target** (cho phép đè lên nhau nếu 2 target
   thật sự cùng hướng — đúng yêu cầu "mũi tên có thể trùng").
4. **Nhãn chữ** thì lùi thêm vào trong ring `stack × 14px` dọc theo đúng tia bearing đó
   (`labelD = edgeD − 26 − stack×14`) — target đầu tiên nhãn sát viền ring nhất, target thứ 2 cùng
   hướng nhãn lùi vào trong 14px, thứ 3 lùi thêm 14px nữa… nhờ vậy các nhãn cùng hướng xếp chồng dọc
   theo tia đó (với hướng gần Bắc/Nam nhìn như "1 cái trên 1 cái dưới" đúng nghĩa đen; hướng khác thì
   xếp dọc theo tia bearing tương ứng) thay vì đè thẳng lên nhau. Độ phức tạp O(n²) khi gom nhóm chấp
   nhận được vì app giới hạn tối đa 4 target (giới hạn này sau đó tăng lên 300 — 2026-09-03, xem mục
   "Position Watch & Targets" — nhưng độ phức tạp vẫn không đáng kể, xem chú thích tại đó).

**Target shapes (2026-08-28)** — `TargetPoint` (`Core/Models/TargetPoint.cs`) thêm field
`ExtraPoints` (`List<LatLon>`, tối đa 3 tọa độ nữa ngoài `Lat`/`Lon` chính — xem mục "Position Watch &
Targets" để biết cách nhập ở ConfigForm/TargetsForm) theo yêu cầu "target có thể là 1 vật thể có hình
dạng, vẽ bằng tọa độ — VD hình chữ nhật thì vẽ 4 tọa độ". `DrawTargets()` trong `ConningControl` đọc
thẳng `SystemConfig.Targets` mỗi `OnPaint` (không cần method Update/push riêng — giống cách `MainForm.
RefreshMiniTargets`/`TargetsForm.RefreshData` đã đọc trực tiếp), chỉ vẽ target `Enabled`, chiếu từng
đỉnh (`Lat`/`Lon` + `ExtraPoints`) qua `GeoMath.OffsetMeters()` và scale theo **cùng `RangeMeters`**
với track: 1 đỉnh → chấm tròn nhỏ + tên, 2 đỉnh → đoạn thẳng, ≥3 đỉnh → đa giác đóng kín
(`g.DrawPolygon`). Màu riêng `Palette.RadarTargetShape` (magenta — màu quy ước hàng hải cho mốc/vùng
nguy hiểm trên hải đồ), tách biệt khỏi mọi màu khác đã dùng trên ring (cam=tàu, xanh lá=lateral/track,
xanh dương=axial, tím=heave, đỏ=drift). Không giới hạn theo RANGE hiện tại (target ngoài range vẽ
tràn ra ngoài ring, giống track) — grid sống ở `TargetsForm`/mini card vẫn chỉ hiện bearing/distance
tới **đỉnh chính** (`Lat`/`Lon`), không tính centroid/cạnh gần nhất cho target dạng hình (giữ đơn giản).

**Lịch sử 3 góc kiểu DP reference** — người dùng gửi ảnh chụp màn hình 1 hệ DP reference thật
(dạng Fanbeam/CyScan) và yêu cầu phân tích rồi thêm 3 mục tương ứng. Ban đầu thêm vào `RadarControl`
(lúc đó áp dụng cho cả mini radar trong `MainForm` lẫn radar lớn trong `TrendsForm`, vì cả 2 dùng chung
1 class) — nhưng người dùng phản hồi "thêm nhầm vào radar bên phải", ý muốn 3 góc này nằm trên
`ConningControl` (radar chính giữa) chứ không phải mini radar. Xử lý ban đầu bằng cách bỏ mini radar
card khỏi `MainForm` và port riêng 1 bản 3-góc thứ 2 vào `ConningControl` — tạo ra 2 bản độc lập cùng
ý tưởng ở 2 file khác nhau, và đúng như lo ngại "sửa 1 bên không tự động áp dụng sang bên kia", layout
của `ConningControl` (ZOOM/WIND/DRIFT dưới đây) sau đó lệch hẳn khỏi layout gốc của `RadarControl`
(RANGE trên-phải/WIND dưới-trái/DRIFT dưới-phải, không đổi) qua nhiều lần chỉnh. Cuối cùng — sau khi
thêm target shapes cần 1 track/range thật, người dùng quyết định **gộp hẳn: xoá `RadarControl.cs`**,
chỉ còn 1 bản 4-góc duy nhất trong `ConningControl` (xem 2 mục RANGE + target shapes ngay phía trên) —
chấm dứt vấn đề đồng bộ 2 file. Lịch sử tiến hoá của ZOOM/WIND/DRIFT ngay dưới đây vẫn giữ nguyên giá
trị tham khảo dù `RadarControl` đã không còn tồn tại để so sánh nữa. **Cập nhật 2026-09-03: góc ZOOM
mô tả trong lịch sử ngay dưới đây (`DrawZoomControl`, thanh trượt riêng) đã bị gộp hẳn vào RANGE —
xem mục "Gộp ZOOM vào RANGE" trong "Lưới ô ly" phía trên — control hiện chỉ còn 3 góc (RANGE/DRIFT/
legend), không phải 4 góc như mô tả lịch sử bên dưới.**
- **Góc ZOOM (dưới-phải, sau khi chuyển từ trên-phải):** ban đầu port y hệt "RANGE" của `RadarControl`
  (chỉnh bán kính hiển thị tính bằng mét) — nhưng lúc đó `ConningControl` chưa có track/bản đồ nào để
  "range" có ý nghĩa thật, chỉ dùng để đổi nhãn số trên vòng lưới, nên người dùng yêu cầu đổi hẳn ý
  nghĩa: **`DrawZoomControl()`** — hộp "ZOOM" phóng to/thu nhỏ **hình tàu + mũi tên vận tốc**, không
  phải vòng lưới (RANGE thật, gắn với track/target, được thêm lại riêng biệt sau này — xem mục RANGE
  ở trên). Qua 2 lần chỉnh theo phản hồi người dùng:
  1. Lần 1 — nút `+`/`−` rời rạc (`ZoomLevels = {0.5,0.75,1.0,1.25,1.5,2.0}`, `_zoomIndex`).
  2. Lần 2 (hiện tại) — **thanh trượt dọc kéo-thả liên tục** ("thay bằng thanh zoom cho trực quan"):
     `_zoomLevel` (double, liên tục, `ZoomMin=0.5`…`ZoomMax=2.0`, mặc định 1.0), max ở **trên**/min ở
     **dưới** (cùng quy ước thanh trượt âm lượng/zoom bản đồ quen thuộc). `_zoomTrackRect` (1
     `RectangleF` bao vùng bấm/kéo, rộng hơn track hiển thị 1 chút cho dễ trúng) tính lại mỗi
     `OnPaint`, đọc lại bởi `OnMouseDown`/`OnMouseMove`/`OnMouseUp` — bấm ở đâu trên track nhảy tới đó
     ngay (`OnMouseDown` gọi `SetZoomFromY` luôn, không cần đợi kéo), kéo tiếp tục cập nhật liên tục
     qua `OnMouseMove` khi `_draggingZoom=true`. `SetZoomFromY(y)` quy đổi vị trí Y trong track sang
     `_zoomLevel` bằng nội suy tuyến tính đơn giản, `Math.Clamp` chặn ngoài track.
  3. **Lăn chuột (2026-08-28):** `OnMouseWheel` override — lăn ở **bất kỳ đâu trên control** (không
     cần rê đúng vào thanh trượt nhỏ) cũng chỉnh được zoom, mỗi nấc lăn (`e.Delta / 120`,
     `SystemInformation.MouseWheelScrollDelta`) = ±0.1×, cùng `Math.Clamp` chặn trong
     `[ZoomMin,ZoomMax]` như thanh trượt.
  `ShipZoom` (= `_zoomLevel`) nhân trực tiếp vào `sl`/`sw` (kích thước tàu) và `arrowScale`/`arrowMax`
  (độ dài mũi tên vận tốc) ngay đầu `OnPaint` — **ring/vòng lưới giữ nguyên kích thước cố định `R`,
  chỉ hình tàu + mũi tên phóng to/thu nhỏ bên trong ring**. `bowY`/`sternY` tự động scale theo vì tính
  từ `sl` (đã nhân zoom) nên không cần sửa riêng. Vòng lưới (`grid circles`) trong `DrawCompassRing()`
  **không còn nhãn khoảng cách** — chỉ còn là 3 vòng tròn trang trí, vì không có ý nghĩa "khoảng cách
  thật" nào để gắn vào nữa.

**Diễn biến ZOOM/WIND/DRIFT sau khi lệch khỏi layout gốc của `RadarControl`** (RANGE trên-phải/WIND
dưới-trái/DRIFT dưới-phải, không đổi — nhưng file đó giờ đã xoá, chỉ còn giá trị lịch sử):
- **ZOOM chuyển xuống góc dưới-phải** (từ trên-phải) — hộp rộng 40→46px để hết bị cắt chữ "ZOOM"
  thành "ZOO" (bug hiển thị đã gặp thực tế, do box quá hẹp so với font 7.5pt bold).
- **DRIFT chuyển lên góc trên-phải** (từ dưới-phải) — nhường chỗ dưới-phải cho ZOOM, đồng thời bọc
  thêm khung nền/viền (trước đó DRIFT chỉ là text trần không có box).
- **WIND (dưới-trái) trải qua 3 lần chỉnh rồi bị bỏ hẳn:** (1) bọc khung giống ZOOM/DRIFT (trước đó
  là text trần), bớt xuống chỉ còn hướng gió, bỏ tốc độ + đơn vị "m/s" — card WIND SPD ở cột trái
  MainForm đã hiển thị đủ tốc độ gió rồi; (2) phóng to khung bằng khung legend X/Y/Z góc trên-trái
  (180×92px); (3) **xoá hẳn `DrawWindCorner()`** — hướng+tốc độ gió vẫn đọc được qua card WIND SPD/
  WIND DIR và qua chính mũi tên gió #9 trên ring, nên góc riêng cho WIND là dư thừa. Ô trống ở
  dưới-trái sau đó được RANGE (xem mục RANGE ở trên) chiếm lại. `ConningControl` hiện có **4 góc**:
  legend (trên-trái), DRIFT (trên-phải), RANGE (dưới-trái), ZOOM (dưới-phải).
- **DRIFT** — `UpdateDrift(enabled, bearingDeg, distanceM)` tái dùng thẳng Position Watch đã có
  (`SystemConfig.DriftWatchEnabled`, khoảng cách từ `GeoMath.DistanceMeters`), bổ sung thêm
  **bearing** (`GeoMath.BearingDeg(RefLat,RefLon → GpsLat,GpsLon)`, hướng từ điểm tham chiếu tới vị
  trí hiện tại) mà Position Watch trước đây chỉ có khoảng cách, không có hướng. Chỉ hiện khi
  `DriftWatchEnabled=true` và có fix GPS — ẩn hẳn (không vẽ) khi tắt. `MainForm` tái dùng
  `_driftTag.Value` đã tính sẵn trong `UiTick`; `TrendsForm` không còn radar nên không cần tính drift
  nữa (đã bỏ cùng lúc xoá `RadarControl`).

**TrendChartControl** — 3 mode (`TrendMode.Motion/Nav/Wind`; `Env` — nhiệt độ/độ ẩm từ MeteoService —
đã bị bỏ khỏi UI vì không nằm trong yêu cầu ban đầu của app), combo chọn khung thời gian
**1/2/5/15/30 phút** (mặc định 1 phút — ngắn nhất trước). `Render()` **ghim `AxisX.Minimum/Maximum` =
[now-viewMin, now]** mỗi lần gọi — bắt buộc phải làm vậy, nếu để Chart tự "Auto" nó sẽ auto-fit theo khoảng
dữ liệu THỰC TẾ đang có (mới mở cửa sổ có ít dữ liệu → trục hẹp, rộng dần theo thời gian) thay vì luôn đúng
khung đã chọn.

### ⚠️ Gotcha của `System.Windows.Forms.DataVisualization` (package `1.0.0-prerelease.20110.1`, chưa có bản ổn định)

Package này gây ra các bug thật đã gặp và fix trong quá trình wiring `TrendChartControl` vào UI — **đa số
đều im lặng cho tới khi Chart thực sự được vẽ lần đầu** (hoặc tới khi đổi theme/thu nhỏ control), nên rất
dễ tái phát nếu code liên quan bị đụng vào mà không biết:

1. **`Chart.OnPaint` throw `FileNotFoundException` cho `System.Data.SqlClient`** ngay trong lần vẽ đầu tiên
   (`ChartImage.DataBind()` cố load assembly này dù không dùng SQL gì cả) — exception này rơi vào
   `Application.ThreadException` → kích hoạt crash-loop guard, cả app tự restart.
   **Fix:** `ConningMonitorPRS.csproj` có `<PackageReference Include="System.Data.SqlClient" Version="4.8.6" />`
   — **không được xoá dù trông như không dùng tới**, đây là workaround bắt buộc.
2. **`ChartArea` để `Position`/`InnerPlotPosition` = "Auto" không co giãn theo %** — khi control bị resize
   nhỏ lại, trục trái đứng nguyên vị trí pixel cũ còn trục phải bị cắt mất thay vì co theo tỉ lệ. **Fix:**
   `TrendChartControl.MakeArea()` ghim cứng `area.Position`/`area.InnerPlotPosition` theo phần trăm
   (`.Auto = false` + set X/Y/Width/Height %) thay vì để mặc định.
3. **Màu Chart không tự đổi theo theme (2026-08-28)** — `Palette.ApplyToForm()`/`ApplySwapMap()` (cơ chế
   đổi theme chung của app, xem mục "Theme / Palette") chỉ duyệt `Control.BackColor`/`ForeColor` (+ 1
   nhánh riêng cho `DataGridView`) — hoàn toàn không biết cách chạm vào màu `ChartArea`/`Series`/`Legend`
   bên trong 1 `Chart`, vì các màu đó chỉ được set **1 lần duy nhất** trong `BuildChart()` tại thời điểm
   Palette trả về giá trị gì lúc đó. Bật `IsLightTheme` lên/xuống trong khi 1 `TrendChartControl` đang mở
   (VD mini chart luôn hiện trong `MainForm`, hoặc `TrendsForm` để mở khi đổi theme) khiến chart bị "kẹt"
   màu theme cũ trong khi cả app đã đổi. **Fix:** `TrendChartControl` tự subscribe
   `SystemConfig.ThemeChanged` trong constructor (gọi lại `BuildChart()` để đọc `Palette.*` mới, unsubscribe
   trong `Dispose(bool)` override) — không dựa vào `Palette.ApplyToForm()` như các control WinForms thường
   khác, vì cơ chế đó không với tới được.
4. **`Legend` (`Docking.Top`) đè lên đúng phần biểu đồ ngay dưới nó khi panel thấp (2026-08-28)** — bình
   thường `Legend.Docking=Top` khiến Chart tự động chừa 1 dải không gian phía trên cho legend, nhưng gotcha
   #2 ở trên (`area.Position.Auto=false`) vô tình **tắt luôn** cơ chế tự chừa chỗ đó — legend chỉ vẽ đè lên
   bất kỳ chỗ nào `area.Position` đang chiếm, nên trên panel thấp (VD mini chart trong `MainForm`, chỉ cao
   ~150-200px) chữ chú thích "Roll/Pitch/Heave" trông như bị chèn ngay vào đường biểu đồ bên dưới. **Fix:**
   ghim cứng `Legend.Position` theo % giống hệt cách `MakeArea()` đã làm cho `ChartArea` — legend chiếm dải
   cố định `Y=0→8%`, còn `area.Position.Y` dời xuống `9%` (từ `2%`) để bắt đầu ngay sau dải đó, cạnh dưới
   giữ nguyên `98%` như cũ (chỉ dời điểm bắt đầu, không đổi lề dưới).

### ⚠️ Gotcha của `SplitContainer`

`SplitterDistance`/`Panel1MinSize`/`Panel2MinSize` đều **validate ngay lập tức theo `Width` hiện tại của
control lúc set property** — set trong object initializer (trước khi control được `Controls.Add()` vào
form) sẽ throw `ArgumentOutOfRangeException`/`InvalidOperationException` vì lúc đó `Width` vẫn là giá trị
mặc định rất nhỏ (`Dock=Fill` chưa có hiệu lực). Thậm chí set ngay sau `Controls.Add()` cũng **chưa chắc an
toàn** vì layout không resolve đồng bộ. Cách an toàn duy nhất: set các property này trong `Form.Load`,
thời điểm duy nhất đảm bảo layout thật đã chạy xong. `TrendsForm.cs` **không còn dùng `SplitContainer`
nữa** (radar panel đã bị gộp/xoá 2026-08-28, giờ chỉ còn 1 `TrendChartControl` dock `Fill`) — ví dụ còn
áp dụng gotcha này hiện tại là `ConfigForm.cs`'s Alarm History tab (`SetupHistoryTab`).

---

## Satellite Status

`SatelliteForm` — **đã có nút mở lại trong bottom bar** ("🛰 GNSS HEALTH", 2026-09-07, xem mục
"PRS-GNSS-01" ngay dưới — form này giờ gộp cả satellite status cũ lẫn health mới, đổi tên hiển thị
thành "GNSS HEALTH / SATELLITE STATUS"). Parse `$GSV` (số vệ tinh nhìn thấy + SNR trung bình theo từng
constellation, gộp qua nhiều sentence trong 1 chu kỳ) và `$GSA` (fix type 2D/3D + PDOP/HDOP/VDOP) trong
`NmeaParserService`. Màu dòng: xanh lá SNR≥35dB, trắng 20-35dB, đỏ <20dB hoặc dữ liệu cũ >5s.

⚠️ **Không phải** vệ tinh phát tín hiệu hiệu chỉnh vi sai (differential correction / SBAS) như một số DGNSS
display chuyên dụng khác — cần bản tin độc quyền của hãng receiver, ngoài phạm vi NMEA-0183 chuẩn.

---

## PRS-GNSS-01 (GNSS Health module — DP-OA handover doc, module 1/2)

Tích hợp đầu tiên trong bộ tài liệu bàn giao "DP Operator Assistant" (2 module: PRS-GNSS-01 tình trạng
GNSS/DGPS + PRS-PQE-01 chất lượng vị trí — **cả 2 module đã triển khai**, xem mục "PRS-PQE-01" ngay
dưới). Bám sát sơ đồ pipeline của tài liệu (mục 1): `Máy thu GNSS/DGPS → PRS-GNSS-01 → LÕI DP-OA →
Khuyến cáo cho DPO/HMI → Log Sự kiện + Dữ liệu Thô` — tách thành 3 lớp riêng biệt thay vì gộp tắt:

- **`Services/Parsing/NmeaParserService.cs`** — thêm event `OnGgaExtendedParsed(port, satsUsed, hdopGga,
  dgpsAgeSec, stationId)`, đọc thêm field 7 (satellites used)/8 (HDOP)/13 (tuổi hiệu chỉnh vi sai)/14
  (mã trạm tham chiếu) của `$GGA` — trước đây parser chỉ đọc tới field 6 (fix quality). Field 13/14
  thường rỗng ngoài chế độ DGPS/RTK, `TryParse` an toàn trả về `NaN`/`""` thay vì fail cả sentence.
- **`Services/GnssHealthEvaluator.cs`** (PRS-GNSS-01) — rule engine **thuần tính toán**, không tự ghi
  hub/log/Tag. `Evaluate(Snapshot)` → `GnssHealthStatus` (9 kênh H1-H9 + Overall + Advisories). Ánh xạ:
  | Kênh | Nguồn dữ liệu | Ghi chú |
  |---|---|---|
  | H1 Data availability | tuổi `TaskRows["GPS"]`, ngưỡng `SystemConfig.GnssDataTimeoutSeconds` | on-delay qua `PersistenceTimer` |
  | H2 Position solution | `GsaFixType` (3D/2D/NO FIX) | |
  | H3 Satellite geometry | `Hdop`/`Pdop`, tier `GnssHdop*`/`GnssPdop*` | worst-of 2 chỉ số |
  | H4 Constellation | đếm constellation có `CountInView>0` trong `Satellites` (từ GSV) | **xấp xỉ** — GSA không tách theo constellation trong app này |
  | H5 Differential correction | `DgpsAgeSec`, chỉ đánh giá khi `GpsFixQuality` là DGPS/RTK | standalone → UNKNOWN (đúng ý tài liệu) |
  | H6 Receiver/antenna | — | **luôn UNKNOWN** — cần bản tin độc quyền hãng, ngoài phạm vi NMEA-0183 |
  | H7 Data link | cùng tuổi `TaskRows["GPS"]` như H1 nhưng ngưỡng ×3 | xem hạn chế bên dưới |
  | H8 Position integrity | so độ lệch vị trí thật (Haversine) vs độ lệch ngụ ý từ SOG mỗi tick | `GnssPositionJumpWarningM` |
  | H9 Trend | rolling buffer 10 mẫu HDOP/SatsUsed (1 mẫu/giây) | Degraded nếu xu hướng xấu dần liên tục |
- **`Services/DpOaCore.cs`** (LÕI DP-OA) — nhận `GnssHealthStatus` từ evaluator mỗi giây
  (`MainForm.HealthTick`), so từng kênh với lần trước để phát hiện chuyển trạng thái → gọi
  `DataLogger.LogRuleEvent` cho mỗi lần đổi (RuleID/kênh/from/to/value/threshold/evidence) → rồi mới
  `ConningDataHub.UpdateGnssHealth(status)` cho HMI đọc. Từ khi có PRS-PQE-01 (module 2, `IngestPqeStatus`
  — xem mục "PRS-PQE-01" ngay dưới), class này còn tính `RecomputeCombined()` áp bảng kết hợp mục 8 tài
  liệu (GNSS×PQE → 1 thông điệp) mỗi khi 1 trong 2 module báo cáo.
- **`Core/Models/HealthState.cs`/`RuleResult.cs`/`GnssHealthStatus.cs`/`PersistenceTimer.cs`** — model
  dùng chung: `enum HealthState { Unknown, Healthy, Degraded, Warning, Invalid }` (Unknown **không phải**
  Healthy — không có dữ liệu thì hiện rõ là không có dữ liệu, không tự nâng lên Healthy), `RuleResult`
  (1 kênh, luôn kèm RuleID/metric/threshold/evidence để truy vết), `PersistenceTimer` (tách từ cơ chế
  on-delay của `Alarm.Evaluate()`, dùng chung cho H1-H5 thay vì chép tay).
- **Alarm/Tag** — `AL_GNSS_WARNING`/`AL_GNSS_INVALID` (Tag `GnssSeverity`, ordinal 0-3 từ
  `HealthState.Severity()`) đăng ký y hệt các alarm khác trong `MainForm.InitServices()`, tái dùng
  nguyên `Alarm`/`AlarmEngine`/confirm-delay/hysteresis đã có — không phải cơ chế báo động mới.
- **Config** (4 lớp `AppConfig`→`SystemConfig`→`ConfigForm`→consumer, đúng pattern
  `MotionConfirmSeconds`) — tất cả ngưỡng H1/H3/H5/H8 đều cấu hình được ở ConfigForm ▸ tab **"GNSS
  Health"** mới: `GnssDataTimeoutSeconds`, `GnssConfirmSeconds`, `GnssHdopDegraded/Warning/Invalid`,
  `GnssPdopDegraded/Invalid`, `GnssCorrectionAgeDegraded/Warning/Invalid`, `GnssPositionJumpWarningM`.
- **UI** — `SatelliteForm` mở rộng (xem mục "Satellite Status" ở trên): badge Overall + bảng 9 kênh +
  advisory WHAT/WHY/IMPACT/ACTION của cảnh báo nghiêm trọng nhất, phía trên grid vệ tinh cũ. Nút mở lại
  trong bottom bar (`_satelliteForm`, cùng pattern `_dataListForm`).
- **Log** — `DataLogger.LogRuleEvent(...)` (dòng CSV `Type=RULE`, tái dùng đúng hạ tầng queue/flush 10s/
  xoay file 30 phút/dọn >90 ngày của `LogAlarmEvent`) ghi mọi lần chuyển trạng thái kênh; `LogSnapshot`
  thêm cột `SatsUsed,Hdop,Pdop,Vdop,DgpsAgeSec,GnssOverall` vào dòng `DATA` định kỳ (`MainForm.LogTick`).
- **Simulation** — `SimulationEngine` thêm 3 chu kỳ độc lập chồng lên nhau (không phải kịch bản FAT có
  kịch bản): HDOP/PDOP/VDOP dao động sin ~2 phút biên độ 0.8-5.0 (test tier H3/xu hướng H9), cửa sổ mất
  fix ~4s mỗi 150s (test H1/H2/H7 → INVALID rồi hồi phục), cửa sổ DGPS ~10s mỗi 90s với tuổi hiệu chỉnh
  tăng/giảm 0→15→0s (test H5, vốn luôn UNKNOWN ở chế độ standalone).

**Hạn chế đã biết (ghi rõ, không che giấu):**
- H6 luôn UNKNOWN — không có bản tin độc quyền hãng để đánh giá receiver/antenna qua NMEA-0183 chuẩn.
- H4 là xấp xỉ (đếm constellation có SV nhìn thấy qua GSV), không phải breakdown "used-in-fix" thật theo
  từng constellation — `NmeaParserService`'s GSA không gắn tag constellation.
- H1 và H7 hiện dùng **chung 1 timestamp** (`ConningDataHub._lastUpdate["GPS"]`, bị ghi đè bởi cả
  `UpdateGpsData` lẫn `UpdateRawString`) — chỉ khác ngưỡng (H1 ngắn/H7 dài gấp 3), chưa phải 2 tín hiệu
  độc lập thật (cần hub tách riêng "tuổi fix hợp lệ" vs "tuổi raw NMEA" nếu muốn tách hẳn sau này).
- Chỉ 1 nguồn GPS (DUO đã tắt, xem mục "DUO GPS mode") — health hiện tại là của nguồn GPS duy nhất,
  không có per-receiver breakdown.

---

## PRS-PQE-01 (Position Quality module — DP-OA handover doc, module 2/2)

Module thứ hai, hoàn tất bộ tài liệu bàn giao DP-OA cùng PRS-GNSS-01 ở trên. Đánh giá **hành vi thực
tế** của luồng vị trí (nhiễu, trôi, nhảy vọt, đóng băng, tính nhất quán động) — độc lập với việc máy
thu GNSS có "báo khoẻ" hay không (1 PRS có thể HEALTHY ở PRS-GNSS-01 nhưng WARNING ở đây, và ngược
lại — đúng tinh thần mục 8 tài liệu). Không đọc thêm NMEA — tiêu thụ thẳng luồng vị trí đã chuẩn hoá
sẵn có trong `ConningDataHub.Snapshot` (`GpsLatDeg/GpsLonDeg/GpsSpeedKnot`), đúng sơ đồ `Máy thu GNSS/
DGPS → Vị trí Thô/Đã chuẩn hóa → PRS-PQE-01 → LÕI DP-OA → Khuyến cáo DPO/HMI → Log`.

- **`Services/PqeEvaluator.cs`** — rule engine **thuần tính toán** (không tự ghi hub/log/Tag), cùng
  hợp đồng với `GnssHealthEvaluator`: `Evaluate(Snapshot)` → `PqeStatus` (8 kênh Q1-Q8 + Overall +
  Advisories), gọi mỗi giây từ `MainForm.HealthTick` ngay sau khối GNSS. Pipeline nội bộ: chiếu vị trí
  sang mét cục bộ (`GeoMath.OffsetMeters`, origin chốt ở fix hợp lệ đầu tiên) → tiền lọc trung vị (5
  mẫu) → lọc nhanh (EMA τ≈5s) + lọc chậm (EMA τ≈60s) → residual = trung vị − lọc nhanh → RMS(30s)/
  R95(60s) từ buffer residual 60s → drift từ buffer lọc chậm 65s → jump/freeze từ so sánh tick liên
  tiếp trừ displacement kỳ vọng theo SOG → vận tốc suy ra so với SOG.
  | Kênh | Nguồn dữ liệu | Ghi chú |
  |---|---|---|
  | Q1 Input data quality | tuổi `TaskRows["GPS"]`, ngưỡng `SystemConfig.GnssDataTimeoutSeconds` | không qua warm-up, tính riêng trong `PqeEvaluator` (không phụ thuộc PRS-GNSS-01) |
  | Q2 Short-term noise | RMS(30s) của residual | tier `PqeRms30*` |
  | Q3 Position stability | R95(60s) của residual | tier `PqeR95*` |
  | Q4 Drift | tốc độ dịch chuyển tâm lọc chậm (m/phút) + hướng (atan2 cục bộ) | tier `PqeDrift*` |
  | Q5 Jump/Freeze | worst-of(jump tier, freeze state) | jump tier `PqeJump*`; freeze `PqeFreezeThresholdM` — tích luỹ displacement ngụ ý từ SOG trong lúc vị trí không đổi |
  | Q6 Dynamic consistency | \|vận tốc suy ra − SOG\| | tier `PqeVelMismatch*` — **SOG cùng nguồn GPS đang đánh giá, không phải log/gyro độc lập thật** |
  | Q7 Cross-PRS consistency | — | **luôn UNKNOWN** — chỉ 1 nguồn vị trí (DUO tắt), đúng H6 pattern của PRS-GNSS-01 |
  | Q8 Trend quality | đếm Q2/Q3/Q4/Q5/Q6 không Healthy | ≥3 kênh suy giảm cùng lúc → Warning |
  Warm-up: Q2/Q3/Q4/Q5/Q6/Q8 UNKNOWN cho tới đủ `SystemConfig.PqeWarmupSamples` mẫu (mặc định 30, ~30s
  ở nhịp 1Hz của `HealthTick`) — Q1/Q7 không qua warm-up.
- **`Services/DpOaCore.cs`** — `IngestPqeStatus(PqeStatus)` (song song `IngestGnssStatus`, cùng cơ chế
  log-transition-rồi-publish-hub qua `UpdatePqeStatus`). Sau mỗi lần 1 trong 2 module `Ingest*`,
  `RecomputeCombined()` chạy (chỉ khi **cả 2** đã báo cáo ít nhất 1 lần) — `BuildCombinedText(gnss,
  pqe)` áp bảng mục 8 tài liệu (Healthy+Healthy, Degraded+Healthy, Healthy+Warning "tình huống quan
  trọng", Warning+Warning, Unknown+*, *+Unknown, + 1 nhánh mặc định worst-of-severity cho tổ hợp tài
  liệu không liệt kê) → `ConningDataHub.UpdateCombinedStatus(text, severity)`.
- **`Core/Models/PqeStatus.cs`** — cùng hình dạng `GnssHealthStatus` (Overall/Channels/Advisories),
  tái dùng nguyên `HealthState`/`RuleResult`/`Advisory`/`PersistenceTimer` từ PRS-GNSS-01, không có
  base type chung (2 class nhỏ, không đáng thêm interface).
- **Alarm/Tag** — `AL_PQE_WARNING`/`AL_PQE_INVALID` (Tag `PqeSeverity`), đăng ký y hệt
  `AL_GNSS_WARNING`/`AL_GNSS_INVALID`.
- **Config** — tab **"Position Quality"** mới trong `ConfigForm` (`SetupPositionQualityTab`):
  `PqeWarmupSamples`, `PqeRms30Degraded/Warning/Invalid`, `PqeR95Degraded/Warning/Invalid`,
  `PqeDriftDegraded/Warning/Invalid`, `PqeJumpDegraded/Warning/Invalid`, `PqeFreezeThresholdM`,
  `PqeVelMismatchDegradedKn/WarningKn`. **Bug layout đã phát hiện & sửa cùng lúc**: tab "GNSS Health"
  (thêm ở PRS-GNSS-01) xếp nội dung tới `Top=517`, vượt quá chiều cao khả dụng của dialog 640×500
  (~406px) — phần Correction Age Warning/Invalid bị cắt mất, không kéo tới được vì `Panel` không tự
  cuộn. Fix: thêm `AutoScroll = true` cho cả `pnlGnss` lẫn `pnlPqe` (tab Position Quality cũng dài
  tương tự, 2 cột `Left=30`/`340` + 1 nhóm phụ Q6 xếp dưới ở `Top=419`).
- **UI** — `UI/Forms/PositionQualityForm.cs` (mới, non-modal, Timer 1000ms riêng, theo đúng pattern
  `SatelliteForm`/`DataListForm`/`TargetsForm`/`TrendsForm`): badge Overall + bảng 8 kênh Q1-Q8 +
  advisory WHAT/WHY/IMPACT/ACTION nghiêm trọng nhất + 1 dòng **"Combined"** (đọc
  `Snapshot.CombinedText`/`CombinedSeverity`). Theo yêu cầu người dùng (2026-09-07), đây là **cửa sổ
  riêng**, không gộp vào `SatelliteForm` — nhưng `SatelliteForm` cũng được thêm cùng 1 dòng "Combined"
  (dưới badge Overall của nó) để dù DPO mở cửa sổ nào trước cũng thấy ngay ý nghĩa kết hợp GNSS×PQE.
  Nút mở trong bottom bar: **"📶 POSITION QUALITY"** (`_positionQualityForm`, cùng pattern
  `_satelliteForm`).
- **Log** — tái dùng `DataLogger.LogRuleEvent(...)` y hệt PRS-GNSS-01, không cần sửa gì thêm (đã
  generic theo `channelId`/`ruleId`). **Không** mở rộng cột `DATA` định kỳ cho RMS/R95/drift ở lần
  tích hợp này (khác PRS-GNSS-01) — Q1-Q8 đã đủ truy vết qua `LogRuleEvent` mỗi lần đổi trạng thái;
  để lại như polish tuỳ chọn nếu cần export số liệu PQE liên tục sau này.
- **Simulation** — không sửa `SimulationEngine` cho module này; chuyển động dead-reckoning + jitter
  heading/SOG đã có sẵn (xem PRS-GNSS-01) đủ để RMS(30s)/R95(60s) không phải hằng số 0 chết cứng, và
  các cửa sổ mất-fix/DGPS mô phỏng của PRS-GNSS-01 tự nhiên kích hoạt Q5 (freeze, khi vị trí đứng yên
  trong lúc GSA báo NO FIX) — không cần kịch bản mô phỏng riêng cho PQE.

**Hạn chế đã biết (ghi rõ, không che giấu):**
- Q7 luôn UNKNOWN — chỉ 1 nguồn vị trí (DUO GPS đã tắt), không có gì để so sánh chéo.
- Q6 dùng `GpsSpeedKnot` ($VTG) từ **cùng** máy thu GPS đang được đánh giá, không phải nguồn tham
  chiếu chuyển động độc lập thật (gyro log/EM log) như tài liệu hình dung — vẫn hữu ích để bắt lỗi nội
  tại luồng vị trí, không phải kiểm tra chéo độc lập hoàn toàn.
- Không có biểu đồ tán xạ "đám mây vị trí" (tài liệu mục 12 gợi ý) — chỉ bảng + advisory text, nhất
  quán với quyết định phạm vi V1 đã áp dụng cho PRS-GNSS-01.
- `LogSnapshot` (dòng `DATA` định kỳ) chưa có cột RMS/R95/drift — chỉ `LogRuleEvent` (dòng `RULE`) ghi
  lại các lần chuyển trạng thái Q1-Q8.

---

## Hệ thống cấu hình

**File:** `Config/config.json` (JSON, load/save qua `ConfigService`)  
**Form:** `ConfigForm` (mở bằng nút SETTINGS, yêu cầu đăng nhập) — 4 tab: **Alarm Limits** (limits + LOA/LOB/
GpsOffset), **COM Config** (bảng port/baud + DUO GPS toggle), **Alarm History**, **Position Watch**
(đổi tên từ "Targets & Watch" 2026-09-03 — không còn sửa target ở đây, xem `TargetsForm`).

### Các tham số cấu hình

| Tham số            | Mặc định | Mô tả                                    |
|---------------------|----------|------------------------------------------|
| AdminPassword       | "123456" | Mật khẩu trang cài đặt                  |
| IsSimulationMode    | true     | Dùng dữ liệu giả thay COM thật          |
| IsLightTheme        | false    | Giao diện sáng/tối                       |
| WindMax             | 40.0     | Ngưỡng báo động gió (m/s)                |
| RMax                | 3.0      | Ngưỡng báo động Roll (°)                 |
| PMax                | 3.0      | Ngưỡng báo động Pitch (°)                |
| HMax                | 2.0      | Ngưỡng báo động Heave (cm)               |
| Loa                 | 100.0    | Chiều dài tàu (m) — nhập trực tiếp ở ConfigForm ▸ Alarm Limits |
| Lob                 | 20.0     | Chiều rộng tàu (m) — như trên             |
| GpsOffset           | 0.0      | Vị trí GPS so với tâm tàu (m) — như trên  |
| DriftWatchEnabled   | false    | Bật cảnh báo trôi dạt (Position Watch)   |
| DriftRefLat/Lon     | 0.0      | Toạ độ điểm tham chiếu (độ thập phân)    |
| DriftRadiusM        | 500.0    | Bán kính vùng an toàn (m)                |
| Targets             | []       | Tối đa 300 `TargetPoint` (Name/Lat/Lon/Enabled/ExtraPoints — tối đa 5 tọa độ phụ, xem `TargetsForm`) |
| DuoGpsEnabled       | false    | **Tắt (`#if DUO_GPS_ENABLED`), xem mục "DUO GPS mode"** — bật GPS2 làm nguồn dự phòng |
| GpsDuoDivergenceM   | 50.0     | **Tắt (`#if DUO_GPS_ENABLED`)** — ngưỡng cảnh báo lệch giữa GPS1/GPS2 (m) |
| GnssDataTimeoutSeconds | 3.0   | PRS-GNSS-01 H1/H7 — tuổi dữ liệu GPS tối đa trước khi INVALID |
| GnssConfirmSeconds  | 2.0      | PRS-GNSS-01 H1-H5 — thời gian duy trì trước khi đổi trạng thái |
| GnssHdopDegraded/Warning/Invalid | 1.5/2.5/4.0 | PRS-GNSS-01 H3 — tier HDOP |
| GnssPdopDegraded/Invalid | 2.5/4.0 | PRS-GNSS-01 H3 — tier PDOP |
| GnssCorrectionAgeDegraded/Warning/Invalid | 5/10/20 | PRS-GNSS-01 H5 — tuổi hiệu chỉnh vi sai (s) |
| GnssPositionJumpWarningM | 3.0  | PRS-GNSS-01 H8 — ngưỡng nhảy vọt vị trí bất thường (m) |
| PqeWarmupSamples    | 30       | PRS-PQE-01 — số mẫu khởi động trước khi Q2-Q6/Q8 hết UNKNOWN |
| PqeRms30Degraded/Warning/Invalid | 0.5/1.0/2.0 | PRS-PQE-01 Q2 — tier nhiễu ngắn hạn RMS(30s) (m) |
| PqeR95Degraded/Warning/Invalid | 1.0/2.0/3.0 | PRS-PQE-01 Q3 — tier độ ổn định R95(60s) (m) |
| PqeDriftDegraded/Warning/Invalid | 0.10/0.25/0.50 | PRS-PQE-01 Q4 — tier tốc độ trôi (m/phút) |
| PqeJumpDegraded/Warning/Invalid | 1.0/2.0/3.0 | PRS-PQE-01 Q5 — tier nhảy vọt (m) |
| PqeFreezeThresholdM | 0.2      | PRS-PQE-01 Q5 — ngưỡng displacement ngụ ý để coi là đóng băng (m) |
| PqeVelMismatchDegradedKn/WarningKn | 0.3/0.8 | PRS-PQE-01 Q6 — tier lệch vận tốc suy ra vs SOG (knot) |
| Tasks               | []       | Danh sách DeviceTask (port, baud, type) — GPS2 đã tắt, xem mục "DUO GPS mode" |

### Cấu hình baudrate mỗi COM (trong ConfigForm ▸ COM Config)
Có thể thay đổi port name và baud rate cho từng task (GPS / WIND / MRU / HEADING / aux / GPS2).
Lưu vào `config.json`, load lại khi khởi động (COM port/baud cần **restart app** để áp dụng).

**Đổi COM trùng nhau → tự hoán đổi (2026-08-28):** chọn 1 port ở dòng này mà port đó đang được gán
cho dòng khác trong cùng bảng (`_dgvCom`) thì 2 dòng **tự hoán đổi port cho nhau** thay vì để 2 task
cùng trỏ 1 port vật lý (chỉ 1 trong 2 mở được lúc runtime, cái còn lại lỗi âm thầm) — không có
MessageBox chặn lưu. `CellBeginEdit` lưu giá trị port **trước khi sửa** vào `_comPortBeforeEdit`.

**Bug "không đổi ngay lập tức" đã gặp và sửa:** bản đầu bắt swap ở `CellEndEdit` — nhưng với
`DataGridViewComboBoxColumn`, chọn 1 item trong dropdown **không** tự kích `CellEndEdit` ngay; ô vẫn ở
trạng thái "dirty nhưng còn đang edit" cho tới khi người dùng Tab/click ra khỏi ô, nên swap chỉ xảy ra
sau khi rời ô — không "ngay khi bị trùng" như yêu cầu. Fix theo pattern chuẩn của WinForms:
`CurrentCellDirtyStateChanged` → gọi `_dgvCom.CommitEdit(DataGridViewDataErrorContexts.Commit)` ngay
khi `IsCurrentCellDirty` và đang ở cột Port — ép edit commit tức thì, đẩy giá trị mới vào cell (kích
`CellValueChanged`) mà không cần đợi mất focus. Logic swap chuyển từ `CellEndEdit` sang
`CellValueChanged` để bắt đúng thời điểm commit tức thì này. `_isSwappingComPort` (bool guard) chặn
việc chính thao tác gán `row.Cells["Port"].Value = ...` bên trong swap lại tự kích hoạt lại
`CellValueChanged` một lần nữa (đệ quy vô nghĩa, dù không vô hạn nhưng không cần thiết).

---

## Hệ thống báo động

`AlarmEngine` đánh giá `List<Alarm>` mỗi tick (100ms, `MainForm._uiTimer`).  
Các alarm được register trong `MainForm.InitServices()`: `AL_WIND`, `AL_ROLL`, `AL_PITCH`, `AL_HEAVE`,
`AL_DRIFT` (Position Watch, xem mục riêng), `AL_GPSDUO` (DUO GPS divergence — **tắt, `#if
DUO_GPS_ENABLED`**, xem mục "DUO GPS mode"), `AL_GNSS_WARNING`/`AL_GNSS_INVALID` (PRS-GNSS-01 overall
severity, Tag `GnssSeverity` cập nhật mỗi giây từ `DpOaCore.CurrentGnssStatus.Overall`, xem mục
"PRS-GNSS-01"), `AL_PQE_WARNING`/`AL_PQE_INVALID` (PRS-PQE-01 overall severity, Tag `PqeSeverity`, xem
mục "PRS-PQE-01").  
States: `Normal` → `Active` (raised) → `Acked` → `Normal` (cleared).  
UI badge nhấp nháy khi Active, màu vàng khi Acked.

**Confirm-delay debounce (on-delay/pickup timer):** `Alarm` tracks how long `Tag.Value` has stayed
*continuously* above the limit (`_violationStartUtc`). It only raises (`IsActive = true`) once that
streak reaches `ConfirmSecondsProvider()` seconds; any dip back under the limit — even for a single
100ms cycle — resets the streak to zero, so a brief spike/glitch never raises an alarm. Clear side is
unchanged: still uses 5% hysteresis (`Tag.Value <= limit * 0.95`), applied to all 6 alarms.
- `AL_ROLL` / `AL_PITCH` / `AL_HEAVE` use `SystemConfig.MotionConfirmSeconds` (default 1.5s) — kept
  short because wave-driven motion only stays past a near-limit peak for ~1-2s per wave cycle
  (6-12s period); a longer delay would miss genuine excursions, not just filter noise.
- `AL_WIND` uses `SystemConfig.WindConfirmSeconds` (default 2.5s) — wind changes slower than wave
  motion, so a longer delay is safe and also filters short, non-sustained gusts.
- `AL_DRIFT`/`AL_GPSDUO` don't pass a `confirmSecondsProvider` to their `Alarm` constructor →
  default 0s → trip instantly like before (unchanged); they still get the 5% clear hysteresis.
- Configurable in Settings ▸ Alarm Limits tab (`Motion Confirm (s)` / `Wind Confirm (s)`), persisted
  via `config.json` (`AppConfig.MotionConfirmSeconds` / `WindConfirmSeconds`, clamped 0-30s in
  `SystemConfig.Apply()`); applies live without restart, same as the limits themselves.

**AL_HEAVE startup grace period:** `XsensMruProcessor.IsHeaveSettled` is `false` for
`HeaveSettlingSeconds` (default 180s) after every `Reset()` — i.e. every MRU connect/reconnect
(`MruService.ProcessPort()` calls `_processor.Reset()` on each port open). The washout filter +
bias EMA both start from zero state and can overshoot several times the true amplitude in the first
~15-30s (warm-up ramp) before converging. `MainForm`'s `_mruService.OnMotionParsed` handler skips
`_heaveTag.Update(...)` while `!IsHeaveSettled` — the AL_HEAVE alarm input simply isn't refreshed
during this window (holds its last/zero value, never trips), while the **displayed** heave value and
trend chart are unaffected (they read `HeaveCm` directly from the snapshot, not the alarm `Tag`).
AL_ROLL/AL_PITCH are NOT gated — Roll/Pitch come directly from the sensor's own AHRS Euler output,
not the washout integrator, so they don't have this startup-transient problem. No UI badge for this
state (kept deliberately minimal — logic-only gate, no new indicator).

---

## Reliability (crash-loop guard / watchdog / NTP)

`Program.cs`:
- `Application.SetUnhandledExceptionMode(CatchException)` + `Application.ThreadException` + `AppDomain.UnhandledException`
  → log qua `SystemLogger.LogError` rồi gọi `Program.RestartApp()`.
- **`RestartApp()` có crash-loop guard**: đếm số lần crash trong 30s gần nhất (lưu vào `Logs/crash_guard.txt`
  dạng `ticks,count`). Crash liên tiếp → delay trước khi relaunch **tăng dần theo cấp số nhân**
  (500ms × 2^(count-1), tối đa 30s) thay vì relaunch ngay lập tức vô hạn lần — tránh spam CPU/log khi gặp lỗi
  tất định lặp lại (ví dụ lỗi khởi động do phần cứng/config hỏng).
- `MainForm.HealthTick` gọi `Program.ClearCrashGuard()` (xoá `crash_guard.txt`) sau khi app chạy ổn định 60s
  liên tục — một lần crash đơn lẻ không làm chậm lần crash tiếp theo (không cộng dồn backoff mãi mãi).

`MainForm`:
- Background `Thread` (`_watchdogThread`) kiểm tra mỗi 5s: nếu `_uiTimer` không tick trong >15s
  (`_lastUiTickTicks` không cập nhật) → coi UI thread bị treo → gọi `Program.RestartApp()`.
  `UiTick()` cập nhật `_lastUiTickTicks` qua `Interlocked.Exchange` ở dòng đầu tiên.
- `Task.Run(CheckNtpSync)` trong constructor — chạy `w32tm /query /status`, log kết quả (không block UI).
- `FormClosed` dừng/dispose tường minh `_uiTimer`/`_healthTimer`/`_logTimer` và unsubscribe
  `SystemConfig.ThemeChanged` trước khi dispose services (`_mruService`/`_meteoService`/`_comEngine`/`_logger`).

---

## MruService — Xsens MTi XBus Binary (Roll/Pitch/Heave)

| Tham số | Giá trị |
|---|---|
| Task | `"MRU"` trong config (mặc định `COM3` @ `115200` baud) |
| DataHub key | `"R/P/H"` (không phải `"MRU"`) |
| Protocol | XBus MTData2, frame `FA FF 36 LEN [items] CS` |
| DataID dùng | Euler `0x2030` (roll/pitch/yaw °), FreeAcc `0x4030` (m/s²), RateOfTurn `0x8020` (rad/s), SampleTimeFine `0x1060` (10kHz, dt) |
| Checksum | `sum(BID..CS) & 0xFF == 0` |
| Init | `GoToConfig` (`FA FF 30 00 D1`) tối đa 3 lần với ACK verify → `GoToMeasurement` (`FA FF 10 00 F1`) → chờ 800ms |
| Recovery | 5 timeout liên tiếp → đóng port → `ComEngine` watchdog tự mở lại → `InitDevice()` retry |
| Shutdown | `SendGoToConfig()` — dừng read thread rồi gửi lại `GoToConfig` để thiết bị về Config mode |
| Heave DSP | `XsensMruProcessor.UpdateEuler()`, 2 tầng độc lập: **Tầng 1** — rotate FreeAcc→earth-Z → bias EMA liên tục (không branch, alpha trộn giữa τ=60s "có sóng" / τ=3s "đứng yên" theo `stationaryConfidence` = rotRate+accVariance) → LPF 2Hz (pre-filter, chỉ khử nhiễu cảm biến) → **Washout filter bậc 2** (`heave''+2ζωn·heave'+ωn²·heave=acc`, cutoff 0.01Hz/ζ=0.707 Butterworth) — không deadband, không giữ/cắt tín hiệu, tự "xả" êm về 0 khi có bias tồn dư; warm-up ramp 30s đầu dùng cutoff cao hơn (0.05Hz) để hội tụ nhanh. **Tầng 2** (`HeaveCycleAnalyzer`) thống kê Peak/Trough/Period/Amplitude từ Instant Heave, tách biệt hoàn toàn khỏi tích phân. `IsHeaveSettled`/`HeaveSettlingSeconds` (180s) gate độ tin cậy cho alarm, không ảnh hưởng giá trị heave xuất ra |
| Simulation | `SystemConfig.IsSimulationMode=true` → `MruService` tự chạy `System.Timers.Timer` 100ms sinh roll/pitch/heaveAcc hình sin, đẩy qua cùng `XsensMruProcessor` pipeline — không cần `ComEngine`/COM port |

Điểm khác so với NMEA cũ: `NmeaParserService.OnMotionParsed` (từ `$CNTB` v.v.) vẫn còn trong code nhưng
**không được subscribe** trong `MainForm` nữa — nếu cần quay lại nguồn NMEA AHRS cho R/P/H, phải tự resubscribe.

`XsensDetector.FindPort()` — quét registry `HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_2639*` để tự dò cổng COM
ảo của Xsens MTi qua USB (chưa được gọi tự động ở đâu trong `MainForm`, cần gọi thủ công trước khi mở Settings
nếu muốn auto-detect).

> **`MruService.cs` và `Mru/XsensMruProcessor.cs` là port trực tiếp từ HelideckVer2 (đã chạy production trên
> tàu thật)** — không phải code viết lại từ tài liệu. Logic frame parsing/checksum/GoToConfig/DSP heave giữ
> nguyên; phần thêm riêng cho ConningMonitorPRS chỉ là: namespace, `ConningDataHub` thay `HelideckDataHub`, và
> event `OnMotionParsed` (roll/pitch/heave **có dấu**) để `MainForm` lấy heave signed cho zero-crossing period
> — hub vẫn nhận `Math.Abs()` như cũ vì `Alarm.Evaluate()` so sánh `Tag.Value > limit` không tự lấy abs.
> ⚠️ Việc **wiring vào ConningMonitorPRS** (ComEngine task filtering, ConfigForm.Tasks, event glue trong
> MainForm) là mới và chưa chạy với phần cứng thật — nên test lại COM3 @115200 với thiết bị Xsens MTi thật
> trước khi lên tàu, dù engine xử lý bên trong đã proven.
> Lưu ý: `UpdateEuler(...)` được gọi với `rotInputIsDegPerSec` mặc định `true`, trong khi comment DataID
> `IdRoT = 0x8020` ghi "rad/s" — nghịch lý này tồn tại y nguyên trong code gốc HelideckVer2 (có thể do cấu
> hình output thực tế của thiết bị dùng deg/s bất kể tên chuẩn Xsens), giữ nguyên khi port sang, chưa sửa.

---

## Theme / Palette

`Palette.cs` — tất cả màu sắc là property `static` trả về theo `IsLight`.  
Gọi `SystemConfig.IsLightTheme = value` → `ThemeChanged` event → `ApplyTheme()` trong MainForm → `Invalidate()` tất cả control.  
**Không hardcode màu** trong các file UI — luôn dùng `Palette.*`.
`Palette.ClickHint` — màu accent riêng để đánh dấu control có thể click (VD icon "⇄" trên card POSITION/SPEED),
cố tình khác `OkFg` (giá trị số) và bộ màu button để đọc như một dấu hiệu tương tác, không phải dữ liệu.

---

## Quy tắc lập trình

- Mọi thao tác UI phải trên UI thread — dùng `Invoke()` nếu cần từ timer/thread khác
- `ConningDataHub` là singleton thread-safe (`lock(_lockData)`)
- Các custom control: override `OnPaintBackground` → no-op để tránh flicker; với `Label` cần tự cập nhật
  `Text` thường xuyên (VD đồng hồ), no-op không đủ — phải override `OnPaintBackground` để tự fill nền **và**
  set `DoubleBuffered = true` (xem `MainForm.ClockLabel`), nếu không sẽ nhấp nháy do erase/paint tách rời.
- Tạo font/brush/pen trong `using` — không cache Font object
- Không dùng `g.MeasureString` với `int.MaxValue` trong vòng lặp vẽ — O(1) probe duy nhất
- Config file path: `Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "config.json")`
- `SplitContainer.SplitterDistance`/`Panel1MinSize`/`Panel2MinSize` chỉ được set trong `Form.Load`, không
  bao giờ trong object initializer hay ngay sau `Controls.Add()` — xem mục "Radar/Trends window" phía trên.
- `System.Windows.Forms.DataVisualization.Charting.ChartArea` phải ghim `Position`/`InnerPlotPosition` theo
  %, không để "Auto" — xem mục "Radar/Trends window".
- Không xoá `PackageReference System.Data.SqlClient` trong `.csproj` dù có vẻ không dùng tới — cần thiết để
  `Chart` control không crash lúc vẽ (xem mục "Radar/Trends window").

---

## Lệnh thường dùng

```powershell
# Build
dotnet build

# Run (debug)
dotnet run

# Build release
dotnet build -c Release
```

---

## Các lỗi đã biết / hạn chế

- `SimulationEngine` phải lưu vào field trong MainForm — nếu là biến local sẽ bị GC
- COM5 (aux data) chưa có SentenceType được định nghĩa — chỉ giữ chỗ
- `MeteoService` hiện chỉ dùng trong simulation (UpdateMeteoData gọi từ SimulationEngine) — chưa wire Modbus RTU thật
- `MruService` (Xsens XBus binary) chưa test với phần cứng thật — xem cảnh báo trong mục "MruService" ở trên
- `XsensMruProcessor` chỉ tính heave tại CG được đẩy vào DataHub; multi-point (Sensor/Bow/Stern/Custom offset)
  đã implement trong `MruOutput` nhưng chưa có UI nào đọc — cần `Installation.EnablePositionCompensation(true)`
  + `SetBowPoint()`/`SetSternPoint()` nếu muốn dùng
- `XsensDetector.FindPort()` chưa được gọi tự động từ UI — cần wire thủ công vào ConfigForm nếu muốn auto-detect
- `TrendsForm` không giữ lịch sử dữ liệu chart/track khi đóng — mở lại là tích luỹ lại từ đầu (buffer sống
  trong chính instance của control, không lưu trong `ConningDataHub`)
- `SatelliteForm` (giờ "GNSS HEALTH / SATELLITE STATUS") chỉ đọc `$GSV`/`$GSA`/`$GGA` chuẩn NMEA-0183 —
  không hiển thị vệ tinh phát tín hiệu hiệu chỉnh vi sai (differential correction/SBAS), cần bản tin độc
  quyền hãng receiver để làm việc đó; kênh H6 (receiver/antenna) của PRS-GNSS-01 vì vậy luôn UNKNOWN
- PRS-GNSS-01: H1/H7 dùng chung 1 timestamp trong `ConningDataHub` (chỉ khác ngưỡng), chưa phải 2 tín
  hiệu độc lập — xem mục "PRS-GNSS-01" để biết chi tiết
- PRS-PQE-01: Q7 (cross-PRS) luôn UNKNOWN (chỉ 1 nguồn vị trí), Q6 dùng SOG từ cùng GPS đang đánh giá
  (không phải nguồn tham chiếu độc lập thật), không có biểu đồ đám mây vị trí — xem mục "PRS-PQE-01"
- DUO GPS: đã tắt (`#if DUO_GPS_ENABLED`, xem mục "DUO GPS mode") — app hiện chỉ có 1 nguồn GPS
- `System.Windows.Forms.DataVisualization` là package prerelease chưa có bản ổn định — xem 2 gotcha đã ghi
  trong mục "Radar/Trends window" trước khi đụng vào `TrendChartControl`
