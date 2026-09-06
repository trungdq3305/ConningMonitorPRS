using System;

namespace ConningMonitorPRS.Services.Mru
{
    public struct Vec3
    {
        public double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(double k, Vec3 a) => new Vec3(k * a.X, k * a.Y, k * a.Z);
    }

    public class MruInstallationConfig
    {
        public Vec3 SensorFromCg { get; set; } = new Vec3(0, 0, 0);
        public Vec3 BowFromCg { get; set; } = new Vec3(0, 0, 0);
        public Vec3 SternFromCg { get; set; } = new Vec3(0, 0, 0);
        public Vec3 CustomPointFromCg { get; set; } = new Vec3(0, 0, 0);
        public bool EnablePositionCompensation { get; set; } = true;

        public void SetAll(Vec3 sensorFromCg, Vec3 bowFromCg, Vec3 sternFromCg, Vec3 customPointFromCg, bool enableCompensation = true)
        {
            SensorFromCg = sensorFromCg;
            BowFromCg = bowFromCg;
            SternFromCg = sternFromCg;
            CustomPointFromCg = customPointFromCg;
            EnablePositionCompensation = enableCompensation;
        }
    }

    /// <summary>
    /// First-order exponential low-pass, used only as a pre-filter to strip sensor noise off
    /// the acceleration signal before it feeds the washout integrator (Tầng 1).
    /// CutoffHz is a mutable public field so it can be tuned live without losing filter state.
    /// </summary>
    public class LowPassFilter
    {
        public double CutoffHz;
        private bool _initialized;
        private double _y;

        public LowPassFilter(double cutoffHz) => CutoffHz = cutoffHz;

        public double Update(double x, double dt)
        {
            if (!_initialized) { _y = x; _initialized = true; return _y; }
            double rc = 1.0 / (2.0 * Math.PI * CutoffHz);
            double alpha = dt / (rc + dt);
            _y += alpha * (x - _y);
            return _y;
        }

        public void Reset() { _initialized = false; _y = 0; }
    }

    /// <summary>
    /// Tầng 2 — thống kê chu kỳ sóng, hoàn toàn độc lập với thuật toán tích phân ở Tầng 1.
    /// Chỉ nhận tín hiệu Instant Heave (đã mượt hoá) làm input, tự đạo hàm ra vận tốc để
    /// dò điểm đổi dấu (zero-crossing của vận tốc) — xác định đỉnh/đáy.
    /// </summary>
    public class HeaveCycleAnalyzer
    {
        public double HeaveInstant { get; private set; }
        public double HeaveUp { get; private set; }
        public double HeaveDown { get; private set; }
        public double HeavePeakToPeak { get; private set; }
        public double HeaveAmplitude { get; private set; }
        public double HeavePeriod { get; private set; }
        public double HeaveFrequency { get; private set; }
        public bool CycleValid { get; private set; }
        public string Status { get; private set; } = "INIT";

        // Ngưỡng thống kê — public để tinh chỉnh theo điều kiện biển thực tế
        public double MinimumPeakToPeak = 0.02;   // (m) biên độ tối thiểu để tính là 1 chu kỳ thật, không phải nhiễu
        public double MinPeriodSeconds = 2.0;     // dải chu kỳ sóng hợp lệ theo yêu cầu CAP 437
        public double MaxPeriodSeconds = 25.0;

        private bool _initialized;
        private double _time;
        private double _prevHeave;
        private double _prevVelocity;
        private double _currentPeak;
        private double _currentPeakTime;
        private double _currentTrough;
        private double _currentTroughTime;
        private bool _hasPeak;
        private bool _hasTrough;
        private double _lastPeakTime = double.NaN;

        public void Reset()
        {
            HeaveInstant = 0;
            HeaveUp = 0;
            HeaveDown = 0;
            HeavePeakToPeak = 0;
            HeaveAmplitude = 0;
            HeavePeriod = 0;
            HeaveFrequency = 0;
            CycleValid = false;
            Status = "RESET";

            _initialized = false;
            _time = 0;
            _prevHeave = 0;
            _prevVelocity = 0;
            _currentPeak = double.MinValue;
            _currentPeakTime = 0;
            _currentTrough = double.MaxValue;
            _currentTroughTime = 0;
            _hasPeak = false;
            _hasTrough = false;
            _lastPeakTime = double.NaN;
        }

        public void Update(double heave, double dt)
        {
            HeaveInstant = heave;

            if (dt <= 0 || dt > 0.5)
            {
                CycleValid = false;
                Status = "INVALID_DT";
                return;
            }

            _time += dt;

            if (!_initialized)
            {
                _prevHeave = heave;
                _prevVelocity = 0;
                _currentPeak = heave;
                _currentTrough = heave;
                _currentPeakTime = _time;
                _currentTroughTime = _time;
                _initialized = true;
                Status = "WAITING";
                return;
            }

            // Đạo hàm rời rạc của Instant Heave — nguồn duy nhất để dò đỉnh/đáy ở Tầng 2
            double velocity = (heave - _prevHeave) / dt;

            if (heave > _currentPeak)
            {
                _currentPeak = heave;
                _currentPeakTime = _time;
            }

            if (heave < _currentTrough)
            {
                _currentTrough = heave;
                _currentTroughTime = _time;
            }

            // Vận tốc đổi dấu dương → âm nghĩa là vừa đi qua đỉnh (Peak)
            if (_prevVelocity > 0 && velocity <= 0)
            {
                _hasPeak = true;
                HeaveUp = _currentPeak;

                if (!double.IsNaN(_lastPeakTime))
                {
                    double period = _currentPeakTime - _lastPeakTime;
                    if (period >= MinPeriodSeconds && period <= MaxPeriodSeconds)
                    {
                        HeavePeriod = period;
                        HeaveFrequency = 1.0 / period;
                    }
                }

                _lastPeakTime = _currentPeakTime;
                _currentTrough = heave;
                _currentTroughTime = _time;
            }

            // Vận tốc đổi dấu âm → dương nghĩa là vừa đi qua đáy (Trough)
            if (_prevVelocity < 0 && velocity >= 0)
            {
                _hasTrough = true;
                HeaveDown = _currentTrough;
                _currentPeak = heave;
                _currentPeakTime = _time;
            }

            if (_hasPeak && _hasTrough)
            {
                double p2p = HeaveUp - HeaveDown;
                if (p2p >= MinimumPeakToPeak)
                {
                    HeavePeakToPeak = p2p;
                    HeaveAmplitude = p2p / 2.0;
                    CycleValid = HeavePeriod >= MinPeriodSeconds && HeavePeriod <= MaxPeriodSeconds;
                    Status = CycleValid ? "CYCLE_VALID" : "WAITING_PERIOD";
                }
                else
                {
                    CycleValid = false;
                    Status = "SMALL_MOTION";
                }
            }

            _prevHeave = heave;
            _prevVelocity = velocity;
        }
    }

    public class MruOutput
    {
        public double RollDeg, PitchDeg, HeadingDeg;
        public double RollRateDegS, PitchRateDegS, YawRateDegS;
        public double VerticalAcceleration, HeaveVelocity;

        public double HeaveInstantCg;
        public double HeaveInstantAtSensor;
        public double HeaveInstantAtBow;
        public double HeaveInstantAtStern;
        public double HeaveInstantAtCustomPoint;

        public double HeaveUpCg;
        public double HeaveDownCg;
        public double HeavePeakToPeakCg;
        public double HeaveAmplitudeCg;
        public double HeavePeriodSeconds;
        public double HeaveFrequencyHz;

        public bool Valid;
        public bool HeaveCycleValid;
        public string Status = "INIT";

        // Debug snapshot — intermediate values from UpdateEuler, for diagnostics
        public double DbgDt;
        public double DbgFreeAccX, DbgFreeAccY, DbgFreeAccZ;
        public double DbgVerticalAcc;
        public double DbgAccBias;
        public double DbgAccAfterBias;
        public double DbgAccFiltered;          // sau LowPassFilter, trước khi vào Washout ODE
        public double DbgRotRate;
        public double DbgStationaryConfidence;  // 0 (đang có sóng) → 1 (đứng yên hoàn toàn), liên tục — không phải cờ nhị phân
    }

    /// <summary>
    /// Tính toán Roll/Pitch/Heave từ dữ liệu XBus MTData2 của Xsens MTi.
    ///
    /// Kiến trúc 2 tầng độc lập:
    ///   Tầng 1 (trong UpdateEuler): Instant Heave real-time bằng Washout Filter bậc 2
    ///           (damped double integration) — không dùng if/else để giữ/ngắt tích phân,
    ///           không có deadband cắt tín hiệu thô, không dùng Kalman.
    ///   Tầng 2 (HeaveCycleAnalyzer): thống kê Peak/Trough/Period/Amplitude từ tín hiệu
    ///           Instant Heave do Tầng 1 xuất ra — hoàn toàn tách biệt khỏi cơ chế tích phân.
    /// </summary>
    public class XsensMruProcessor
    {
        public MruInstallationConfig Installation { get; private set; } = new MruInstallationConfig();

        // ── TẦNG 1 — WASHOUT FILTER (public, tunable) ───────────────────────────
        // Đáp ứng của hệ h''+2ζωn·h'+ωn²·h=acc: cutoff quá cao thì suy hao/lệch pha nhiều ở
        // sóng chu kỳ dài; cutoff=0.01Hz + ζ=0.707 (Butterworth — đáp ứng phẳng nhất, không có
        // đỉnh cộng hưởng) cho biên độ ≥99.9%, lệch pha 3-16° trên dải sóng biển thực tế (4-20s).
        // Đánh đổi: washout về 0 khi tàu dừng chậm hơn (~5/(ζωn) ≈ 110s). Nếu vùng biển hoạt
        // động chỉ có sóng ngắn (chu kỳ <10s), có thể tăng cutoff lên để washout nhanh hơn mà
        // vẫn giữ độ chính xác — chỉnh trực tiếp 2 field public này.
        /// <summary>Tần số cắt washout (Hz). Thấp hơn → giữ sóng chu kỳ dài chính xác hơn nhưng trôi bias chậm hơn về 0. Mặc định 0.01Hz (ωn≈100s), phù hợp dải sóng 4-20s.</summary>
        public double WashoutCutoffHz = 0.01;
        /// <summary>Hệ số tắt dần ζ của bộ lọc bậc 2. ζ=0.707 (Butterworth) cho biên độ/pha chính xác nhất mà không có đỉnh cộng hưởng; ζ cao hơn (0.9-1.0) làm mượt hơn nhưng suy hao/lệch pha nhiều hơn ở cùng cutoff.</summary>
        public double WashoutDampingRatio = 0.70710678;

        /// <summary>Tần số cắt LPF làm mượt gia tốc trước khi tích phân (loại nhiễu tần số cao của cảm biến).</summary>
        public double AccPreFilterCutoffHz
        {
            get => _accPreFilter.CutoffHz;
            set => _accPreFilter.CutoffHz = value;
        }

        // ── Static bias tracking — hệ số học liên tục (không branch), điều chế theo độ tin cậy "đứng yên" ──
        // Dùng hằng số THỜI GIAN (giây), quy đổi sang alpha mỗi frame qua dt/(τ+dt) — giống
        // LowPassFilter — để hành vi nhất quán bất kể tần số lấy mẫu (MRU thật ~100Hz vs
        // simulator 10Hz).
        /// <summary>Hằng số thời gian (s) học bias khi tàu đang có sóng/di chuyển (chậm để không ăn mất biên độ sóng, nhưng đủ nhanh để không bị washout filter khuếch đại thành offset lớn).</summary>
        public double BiasSlowTimeConstantSeconds = 60.0;
        /// <summary>Hằng số thời gian (s) học bias khi tàu thực sự đứng yên (nhanh để bám đúng offset cảm biến).</summary>
        public double BiasFastTimeConstantSeconds = 3.0;
        /// <summary>Ngưỡng RateOfTurn (deg/s) — dưới ngưỡng này được tính là "không quay".</summary>
        public double StationaryRotThresholdDegS = 0.5;
        /// <summary>Hằng số thời gian (s) ước lượng phương sai gia tốc — quy đổi qua dt/(τ+dt) như bias EMA.</summary>
        public double AccVarianceTimeConstantSeconds = 2.0;
        /// <summary>Ngưỡng phương sai gia tốc (m/s²)² — dưới ngưỡng này được tính là "không rung/lắc".</summary>
        public double StationaryAccVarThreshold = 0.0004;

        /// <summary>Ngưỡng loại bỏ spike gia tốc — chỉ để chặn lỗi cảm biến/va chạm cơ học, không phải logic điều khiển tích phân.</summary>
        public double MaxVerticalAcceleration = 15.0;

        // ── Startup settling guard ───────────────────────────────────────────────
        // Kalman/washout filter cần thời gian hội tụ sau khi khởi động — các hệ heave sensor
        // thương mại (TSS DMS, Xsens MTi...) đều có "filter settling time" và khuyến cáo không
        // tin heave trong vài phút đầu.
        /// <summary>Số giây sau khi Reset() (MRU connect/reconnect) mà heave được coi là chưa đủ tin cậy cho mục đích alarm — dùng để gate AL_HEAVE ở tầng UI/Alarm, không tự động ảnh hưởng tới giá trị heave xuất ra.</summary>
        public double HeaveSettlingSeconds = 180.0;
        private double _timeSinceReset = 0;
        public double TimeSinceResetSeconds => _timeSinceReset;
        public bool IsHeaveSettled => _timeSinceReset >= HeaveSettlingSeconds;

        // ── Warm-up ramp — rút ngắn thời gian hội tụ mà không đổi độ chính xác steady-state ──
        // Trong WarmupSeconds đầu, dùng cutoff cao hơn hẳn (ωn lớn → DC-gain nhỏ → hội tụ nhanh
        // hơn nhiều lần) và ép bias học nhanh bất kể đang có sóng hay không (bootstrapping), sau
        // đó giảm dần mượt (không có bước nhảy nào cả) về đúng tham số đã tinh chỉnh cho
        // steady-state.
        /// <summary>Số giây ramp mượt từ tham số warm-up (hội tụ nhanh) về tham số steady-state (chính xác) đã tinh chỉnh.</summary>
        public double WarmupSeconds = 30.0;
        /// <summary>Cutoff (Hz) dùng khi mới Reset() — cao hơn WashoutCutoffHz để giảm DC-gain, giúp trạng thái ban đầu tắt nhanh hơn.</summary>
        public double WarmupCutoffHz = 0.05;

        private readonly HeaveCycleAnalyzer _cgCycle = new HeaveCycleAnalyzer();
        private readonly LowPassFilter _accPreFilter = new LowPassFilter(2.0);

        private double _accBias;
        private double _accVarEma;
        private double _heaveVelocity;
        private double _heave;

        public void SetSensorPosition(double xMeter, double yMeter, double zMeter)
            => Installation.SensorFromCg = new Vec3(xMeter, yMeter, zMeter);

        public void SetBowPoint(double xMeter, double yMeter, double zMeter)
            => Installation.BowFromCg = new Vec3(xMeter, yMeter, zMeter);

        public void SetSternPoint(double xMeter, double yMeter, double zMeter)
            => Installation.SternFromCg = new Vec3(xMeter, yMeter, zMeter);

        public void SetCustomPoint(double xMeter, double yMeter, double zMeter)
            => Installation.CustomPointFromCg = new Vec3(xMeter, yMeter, zMeter);

        public void EnablePositionCompensation(bool enable)
            => Installation.EnablePositionCompensation = enable;

        public void SetInstallationConfig(MruInstallationConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            Installation = config;
        }

        public void Reset()
        {
            _cgCycle.Reset();
            _accPreFilter.Reset();
            _accBias = 0;
            _accVarEma = 0;
            _heaveVelocity = 0;
            _heave = 0;
            _timeSinceReset = 0;
        }

        public MruOutput UpdateEuler(
            double rollDeg, double pitchDeg, double yawDeg,
            Vec3 freeAccBody, Vec3 rateOfTurnBody, double dt,
            bool rotInputIsDegPerSec = true)
        {
            MruOutput output = new MruOutput();

            if (dt <= 0.0 || dt > 0.2)
            {
                output.Valid = false;
                output.Status = "INVALID_DT";
                return output;
            }

            _timeSinceReset += dt;

            // Plausibility guard: mounted sensor cannot exceed ±90° roll/pitch — larger values
            // indicate a faulty or misconfigured sensor, not a real ship motion.
            if (Math.Abs(rollDeg) > 90.0 || Math.Abs(pitchDeg) > 90.0)
            {
                output.Valid = false;
                output.Status = "SENSOR_FAULT_RPY";
                return output;
            }

            double roll = DegToRad(rollDeg);
            double pitch = DegToRad(pitchDeg);
            double yaw = DegToRad(yawDeg);

            Vec3 accEarth = RotateBodyToEarth(freeAccBody, roll, pitch, yaw);
            double verticalAcc = accEarth.Z;

            // Sensor-fault guard only (mechanical spike / bad frame) — not part of the
            // continuous integration path, does not gate or hold the washout state.
            if (Math.Abs(verticalAcc) > MaxVerticalAcceleration)
            {
                output.Valid = false;
                output.Status = "ACC_SPIKE_REJECTED";
                return output;
            }

            Vec3 rotDegS = rotInputIsDegPerSec
                ? rateOfTurnBody
                : new Vec3(RadToDeg(rateOfTurnBody.X), RadToDeg(rateOfTurnBody.Y), RadToDeg(rateOfTurnBody.Z));
            double rotRate = Math.Sqrt(rotDegS.X * rotDegS.X + rotDegS.Y * rotDegS.Y + rotDegS.Z * rotDegS.Z);

            // ── WARM-UP RAMP: hội tụ nhanh trong WarmupSeconds đầu, ramp mượt về tham số
            // steady-state đã tinh chỉnh — không có bước nhảy nào, chỉ là nội suy tuyến tính
            // liên tục theo _timeSinceReset.
            double warmupProgress = Clamp01(_timeSinceReset / Math.Max(1e-9, WarmupSeconds)); // 0 (vừa reset) → 1 (hết warm-up)
            double effectiveCutoffHz = WarmupCutoffHz + (WashoutCutoffHz - WarmupCutoffHz) * warmupProgress;
            double warmupBiasBoost = 1.0 - warmupProgress; // 1 (vừa reset, ép học nhanh) → 0 (hết warm-up)

            // ── STATIC BIAS: EMA liên tục, tốc độ học điều chế mượt theo "độ tin cậy đứng yên" ──
            // Không có nhánh if/else bật/tắt tích phân — chỉ có 1 hệ số học (biasAlpha) trộn
            // liên tục giữa 2 hằng số thời gian (Slow/Fast) theo stationaryConfidence (0..1),
            // nên không bao giờ "đóng băng" hay "giữ" tín hiệu heave một cách đột ngột.
            double dev = verticalAcc - _accBias;
            double varAlpha = dt / (AccVarianceTimeConstantSeconds + dt);
            _accVarEma += varAlpha * (dev * dev - _accVarEma);

            double rotFactor = Clamp01(1.0 - rotRate / StationaryRotThresholdDegS);
            double accFactor = Clamp01(1.0 - _accVarEma / StationaryAccVarThreshold);
            // Trong warm-up, ép confidence cao (bootstrapping bias nhanh) bất kể có sóng hay
            // không — sau warm-up, hành vi giống hệt trước đây (chỉ phụ thuộc rotFactor×accFactor).
            double stationaryConfidence = Math.Max(rotFactor * accFactor, warmupBiasBoost);

            double biasSlowAlpha = dt / (BiasSlowTimeConstantSeconds + dt);
            double biasFastAlpha = dt / (BiasFastTimeConstantSeconds + dt);
            double biasAlpha = biasSlowAlpha + (biasFastAlpha - biasSlowAlpha) * stationaryConfidence;
            _accBias += biasAlpha * dev;

            double acc = verticalAcc - _accBias; // không deadband — giữ nguyên tín hiệu thô sau khi trừ bias

            // Pre-filter: chỉ loại nhiễu tần số cao của cảm biến, không tác động lên hình dạng sóng thật
            acc = _accPreFilter.Update(acc, dt);

            // ── TẦNG 1: WASHOUT FILTER BẬC 2 (Damped Double Integration) ────────────
            // Heave được mô hình hoá như đáp ứng của một hệ dao động tắt dần bậc 2 bị
            // kích thích bởi gia tốc thẳng đứng:
            //
            //     heave'' + 2·ζ·ωn·heave' + ωn²·heave = acc(t)
            //
            // Hàm truyền tương ứng:  Heave(s)/Acc(s) = 1 / (s² + 2ζωn·s + ωn²)
            //
            //   • Tần số tín hiệu ≫ ωn (sóng biển thật, chu kỳ ~4-20s → f ~0.05-0.25Hz):
            //     đáp ứng hội tụ về 1/s² — đúng bằng tích phân kép lý tưởng, nên biên độ
            //     và pha của heave phản ánh chính xác chuyển động thật.
            //   • Tần số tín hiệu ≪ ωn (trôi cảm biến / bias tồn dư gần DC):
            //     gain hội tụ về hằng số hữu hạn 1/ωn² thay vì tiến ra vô cùng như tích
            //     phân thuần tuý — heave KHÔNG trôi vô hạn, tự "xả" (washout) êm về 0
            //     theo quy luật hàm mũ tắt dần — không có bước nhảy/giữ trạng thái đột ngột
            //     nên sóng đầu ra luôn là đường cong mượt (AC-coupled), không bị méo/kẹp đỉnh.
            //
            // Rời rạc hoá bằng Euler tường minh (dt lấy từ SampleTimeFine, ~10ms — rất nhỏ
            // so với chu kỳ dao động của bộ lọc 1/WashoutCutoffHz, mặc định ~100s, nên ổn định số học).
            // effectiveCutoffHz = WarmupCutoffHz (cao, hội tụ nhanh) trong WarmupSeconds đầu,
            // ramp mượt về WashoutCutoffHz (thấp, chính xác) — không có bước nhảy đột ngột nào.
            double omegaN = 2.0 * Math.PI * effectiveCutoffHz;
            double heaveAccel = acc - 2.0 * WashoutDampingRatio * omegaN * _heaveVelocity - omegaN * omegaN * _heave;
            _heaveVelocity += heaveAccel * dt;
            _heave += _heaveVelocity * dt;

            // Safety clamp — CHỈ áp vào bản sao xuất ra (output), không bao giờ ghi đè lại
            // _heaveVelocity/_heave. Đây là hệ bậc 2 ổn định (ζ,ωn>0 → nghiệm luôn tắt dần
            // với mọi input hữu hạn kể cả bias hằng số), nên clamp state nội bộ là không cần
            // thiết và sẽ phá vỡ đúng động lực học của bộ lọc (reset "chỗ nhớ" của ODE mỗi
            // frame khi chạm biên — về bản chất lại chính là loại nhánh giữ/cắt tín hiệu mà
            // thiết kế này cố tránh). Biên ±20m chỉ để chặn NaN/Inf hoặc lỗi số học, nằm ngoài
            // mọi biên độ sóng thật.
            double heaveInstantCg = Math.Max(-20.0, Math.Min(20.0, _heave));
            // ─────────────────────────────────────────────────────────────────────

            double heaveAtSensor = ApplyPositionCompensation(heaveInstantCg, Installation.SensorFromCg, roll, pitch);
            double heaveAtBow = ApplyPositionCompensation(heaveInstantCg, Installation.BowFromCg, roll, pitch);
            double heaveAtStern = ApplyPositionCompensation(heaveInstantCg, Installation.SternFromCg, roll, pitch);
            double heaveAtCustom = ApplyPositionCompensation(heaveInstantCg, Installation.CustomPointFromCg, roll, pitch);

            // ── TẦNG 2: thống kê chu kỳ sóng — chỉ nhận Instant Heave làm input ─────
            _cgCycle.Update(heaveInstantCg, dt);

            output.RollDeg = rollDeg;
            output.PitchDeg = pitchDeg;
            output.HeadingDeg = Normalize360(yawDeg);
            output.RollRateDegS = rotDegS.X;
            output.PitchRateDegS = rotDegS.Y;
            output.YawRateDegS = rotDegS.Z;

            output.VerticalAcceleration = verticalAcc;
            output.HeaveVelocity = _heaveVelocity;

            output.HeaveInstantCg = heaveInstantCg;
            output.HeaveInstantAtSensor = heaveAtSensor;
            output.HeaveInstantAtBow = heaveAtBow;
            output.HeaveInstantAtStern = heaveAtStern;
            output.HeaveInstantAtCustomPoint = heaveAtCustom;

            output.HeaveUpCg = _cgCycle.HeaveUp;
            output.HeaveDownCg = _cgCycle.HeaveDown;
            output.HeavePeakToPeakCg = _cgCycle.HeavePeakToPeak;
            output.HeaveAmplitudeCg = _cgCycle.HeaveAmplitude;
            output.HeavePeriodSeconds = _cgCycle.HeavePeriod;
            output.HeaveFrequencyHz = _cgCycle.HeaveFrequency;

            output.HeaveCycleValid = _cgCycle.CycleValid;
            output.Valid = true;
            output.Status = _cgCycle.Status;

            output.DbgDt = dt;
            output.DbgFreeAccX = freeAccBody.X;
            output.DbgFreeAccY = freeAccBody.Y;
            output.DbgFreeAccZ = freeAccBody.Z;
            output.DbgVerticalAcc = verticalAcc;
            output.DbgAccBias = _accBias;
            output.DbgAccAfterBias = dev;
            output.DbgAccFiltered = acc;
            output.DbgRotRate = rotRate;
            output.DbgStationaryConfidence = stationaryConfidence;

            return output;
        }

        private static double Clamp01(double x) => x < 0.0 ? 0.0 : (x > 1.0 ? 1.0 : x);

        private double ApplyPositionCompensation(double heaveCg, Vec3 pointFromCg, double rollRad, double pitchRad)
        {
            if (!Installation.EnablePositionCompensation)
                return heaveCg;
            return GetHeaveAtPoint(heaveCg, pointFromCg, rollRad, pitchRad);
        }

        public static double GetHeaveAtPoint(double heaveCg, Vec3 pointFromCg, double rollRad, double pitchRad)
            => heaveCg + pointFromCg.X * Math.Sin(pitchRad) - pointFromCg.Y * Math.Sin(rollRad);

        public static Vec3 RotateBodyToEarth(Vec3 body, double roll, double pitch, double yaw)
        {
            double cr = Math.Cos(roll), sr = Math.Sin(roll);
            double cp = Math.Cos(pitch), sp = Math.Sin(pitch);
            double cy = Math.Cos(yaw), sy = Math.Sin(yaw);

            return new Vec3(
                (cy * cp) * body.X + (cy * sp * sr - sy * cr) * body.Y + (cy * sp * cr + sy * sr) * body.Z,
                (sy * cp) * body.X + (sy * sp * sr + cy * cr) * body.Y + (sy * sp * cr - cy * sr) * body.Z,
                (-sp) * body.X + (cp * sr) * body.Y + (cp * cr) * body.Z
            );
        }

        public static double DegToRad(double d) => d * Math.PI / 180.0;
        public static double RadToDeg(double r) => r * 180.0 / Math.PI;
        public static double Normalize360(double d) { d %= 360; return d < 0 ? d + 360 : d; }
    }
}
