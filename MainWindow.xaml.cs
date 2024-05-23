using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using RJCP.IO.Ports;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using System;
using System.Windows.Controls;
using System.Globalization;
using System.Windows.Data;

namespace apnea_demo
{
    public class MinHeightConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double actualHeight = (double)value;
            var height = Math.Max(500, actualHeight - 225); // ウィンドウの70%を使用しつつ、最低500を保証
            Debug.WriteLine($"MinHeightConverter: {height} : {actualHeight}");
            return height;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException("This converter only works for one-way binding.");
        }
    }

    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        // データの時間間隔
        public double PointSeconds { get; set; } = 0.5; //（秒）

        // 移動平均の時間（秒）
        public double MovingAveragePeriod { get; set; } = 1.2; //（秒）

        // 無呼吸の閾値
        public int ApneaThreshold { get; set; } = 100;

        // ラインの太さ
        public int StrokeThickness { get; set; } = 2; //（ピクセル）

        // 無呼吸ラインの太さ
        public int ApneaStrokeThickness { get; set; } = 4; //（ピクセル）

        // X軸の最大表示時間
        public int AxisSecondsMaximum { get; set; } = 60; //（秒）

        // X軸の最小表示時間
        public int AxisSecondsMinimum { get; set; } = -240; //（秒）

        // Y軸の最大値
        public int AxisValueMaximum { get; set; } = 5000;

        // Y軸の最小値
        public int AxisValueMinimum { get; set; } = -2000;

        // LED表示の更新間隔
        public double LedDisplayInterval { get; set; } = 0.5; //（秒）

        // デバイス検出のポーリング間隔
        public double DeviceDetectPollingSeconds { get; set; } = 1.0; //（秒）

        // デバイスの生存確認ポーリング間隔
        public double DevicePollingSeconds { get; set; } = 3.0; //（秒）

        // シリアルポートの書き込みタイムアウト
        public double SerialPortWriteTimeout { get; set; } = 0.5; //（秒）

        // シリアルポートの書き込みタイムアウト
        public double SerialPortReadTimeout { get; set; } = 0.5; //（秒）

        // 改行コード
        public string NewLine { get; set; } = "\r\n"; 



        public PlotModel PlotModel { get; set; }

        private bool _autoScroll = true;
        public bool IsAutoScroll
        {
            get { return _autoScroll; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private CancellationTokenSource _cancellationTokenSource;

        private string _deviceInfo;
        public string DeviceInfo
        {
            get { return _deviceInfo; }
            set
            {
                if (_deviceInfo != value)
                {
                    _deviceInfo = value;
                    OnPropertyChanged();
                }
            }
        }

        private List<string> _deviceLogList = new List<string>();
        private string _deviceLogs;
        public string DeviceLogs
        {
            get { return _deviceLogs; }
            set
            {
                _deviceLogList.Add(value);
                if (_deviceLogList.Count > 100)
                {
                    _deviceLogList.RemoveAt(0);
                }

                _deviceLogs = string.Empty;
                foreach (var log in _deviceLogList)
                {
                    _deviceLogs += log + NewLine;
                }
                OnPropertyChanged();
            }
        }

        private Brush _connectionStatusColor = Brushes.Red; // 初期状態は通常「未接続」を示す緑色
        public Brush ConnectionStatusColor
        {
            get => _connectionStatusColor;
            set
            {
                if (_connectionStatusColor != value)
                {
                    _connectionStatusColor = value;
                    OnPropertyChanged();
                }
            }
        }
        private DateTime _lastTime;

        private string _connectionStatus;
        public string ConnectionStatus
        {
            get { return _connectionStatus; }
            set
            {
                if (_connectionStatus != value)
                {
                    _connectionStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        private ConcurrentQueue<Tuple<DateTime, int, int, int>> _dataQueue = new ConcurrentQueue<Tuple<DateTime, int, int, int>>();


        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // 通信状態の更新メソッド
        private void ToggleConnectionColor()
        {
            var now = DateTime.Now;

            if (now - _lastTime > TimeSpan.FromSeconds(LedDisplayInterval))
            {
                _lastTime = now;
                if (ConnectionStatusColor != Brushes.Green)
                {
                    ConnectionStatusColor = Brushes.Green;
                }
                else
                {
                    ConnectionStatusColor = Brushes.Blue;
                }
            }
        }

        public void UpdateConnectionStatus(bool isConnected)
        {
            if (!isConnected)
            {
                ConnectionStatusColor = Brushes.Red;  // タイムアウトの場合は赤色
            }
            else
            {
                ToggleConnectionColor();
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            var now = DateTime.Now;

            // グラフの設定
            PlotModel = new PlotModel { Title = "Real-time Data" };
            // X軸の設定
            PlotModel.Axes.Add(new DateTimeAxis
            {
                Position = AxisPosition.Bottom,
                StringFormat = "HH:mm",  // 時間と分の表示
                IntervalType = DateTimeIntervalType.Minutes,
                IntervalLength = (60 * 2) / PointSeconds,  // 軸の間隔のピクセル数
                Minimum = now.AddSeconds(AxisSecondsMinimum).ToOADate(),
                Maximum = now.AddSeconds(AxisSecondsMaximum).ToOADate(),
                IsZoomEnabled = false,
                IsPanEnabled = false,
            });
            // Y軸の設定
            PlotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = "Value",
                Minimum = AxisValueMinimum,
                Maximum = AxisValueMaximum,
                IsZoomEnabled = true,
                IsPanEnabled = true,
            });

            _deviceInfo = string.Empty;
            _deviceLogs = string.Empty;
            _connectionStatus = string.Empty;

            DataContext = this;

            _lastTime = now;

            _cancellationTokenSource = new CancellationTokenSource();

            Loaded += MainWindow_Loaded;
            Unloaded += MainWindow_Unloaded;

        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            StartPlottingTask(_cancellationTokenSource.Token);

            StartDeviceDetection(_cancellationTokenSource.Token);
        }

        private void MainWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            _cancellationTokenSource?.Cancel();
        }

        private void LogsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // スクロールバーをテキストボックスの末尾に移動させる
            LogsTextBox.ScrollToEnd();
        }


        private void StartPlottingTask(CancellationToken cancellationToken)
        {
            LineSeries? seriesBase = null;
            LineSeries? seriesApnea = null;

            // バックグラウンドスレッドでグラフ描画
            _ = Task.Run(() =>
            {
                // 無呼吸の判定用
                bool isApnea = false;
                // 移動平均計算用

                // 移動平均の値を保持するリスト
                var movingAverageValues = new List<Tuple<DateTime, int>>();
                // 現在のラインのデータを保持するリスト
                var currentLine = new List<DataPoint>();

                DateTime? lastTime = null;
                //(DateTime time, int value)? lastCoordinate = null;

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_dataQueue.TryDequeue(out var data))
                    {
                        DateTime time = data.Item1;
                        try
                        {
                            DateTime nextTime;

                            // 無呼吸状態を退避
                            Boolean preApenaStatus = isApnea;

                            // ----------------------------------------------------
                            // 座標データの作成
                            // ----------------------------------------------------
                            (DateTime time, double value) coordinate;
                            try
                            {
                                int x = data.Item2;
                                int y = data.Item3;
                                int z = data.Item4;

                                // 初回は、データの時刻を最終時刻として設定
                                if (lastTime == null)
                                {
                                    lastTime = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, time.Second);
                                }
                                nextTime = lastTime.Value.AddSeconds(PointSeconds);

                                // ----------------------------------------------------
                                // 移動平均の計算
                                // ----------------------------------------------------
                                // 移動平均の値のリストから指定時間より前のデータを削除
                                var threshold = time.AddSeconds(MovingAveragePeriod * (-1));
                                movingAverageValues.RemoveAll(v => v.Item1 < threshold);

                                // 移動平均の値のリストに追加
                                movingAverageValues.Add(new Tuple<DateTime, int>(time, y));

                                // 移動平均の値の合算
                                int sum = 0;
                                foreach (var value in movingAverageValues)
                                {
                                    sum += value.Item2;
                                }

                                double val = sum / movingAverageValues.Count;
                                // 常用対数に変換
                                val = Math.Log10(val);
                                // そのままではグラフに表示できないので、適当に調整
                                val *= 5000;
                                val -= 14000;
                                // 移動平均の値で追加するDataPointを作成
                                coordinate = (time, val);
                            }
                            finally
                            {
                            }

                            if (nextTime > time)
                            {
                                continue;
                            }

                            var timeMin = time.AddSeconds(AxisSecondsMinimum).ToOADate();
                            var timeMax = time.AddSeconds(AxisSecondsMaximum).ToOADate();
                            var newDataPoint = new DataPoint(coordinate.time.ToOADate(), coordinate.value);

                            // UIスレッドでグラフ描画
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (seriesBase == null)
                                {
                                    seriesBase = new LineSeries { StrokeThickness = StrokeThickness, Color = OxyColors.Black };
                                    PlotModel.Series.Add(seriesBase);
                                }

                                if (nextTime > time)
                                {
                                    if (seriesBase.Points.Count > 0)
                                    {
                                        seriesBase?.Points.RemoveAt(seriesBase.Points.Count - 1);
                                    }
                                }
                                seriesBase?.Points.Add(newDataPoint);

                                if (time >= nextTime)
                                {
                                    //Debug.WriteLine($"next: {nextTime} time: {time}.{time.Millisecond,D3} {coordinate.value,5} Count: {movingAverageValues.Count}");  // コンソールに受信データを出力
                                    // オートスクロールの場合は、X軸の範囲を更新
                                    if (IsAutoScroll)
                                    {
                                        foreach (var axis in PlotModel.Axes)
                                        {
                                            // X軸の設定を検索
                                            if (axis.Position == AxisPosition.Bottom)
                                            {
                                                // X軸の範囲を更新
                                                axis.Minimum = timeMin;
                                                axis.Maximum = timeMax;
                                                break;
                                            }
                                        }
                                    }

                                    // X軸の範囲から外れたデータを削除
                                    var series = (LineSeries?)PlotModel.Series.FirstOrDefault();
                                    if (series != null)
                                    {
                                        var lastDataPoint = series.Points.Last();
                                        if (lastDataPoint.X < timeMin)
                                        {
                                            PlotModel.Series.Remove(series);
                                        }
                                    }
                                }

                                // グラフを再描画
                                PlotModel.InvalidatePlot(true);
                            }), DispatcherPriority.Background);


                            // ----------------------------------------------------
                            // 無呼吸の判定ロジック
                            // ----------------------------------------------------
                            // 現在のラインのデータが2点以上の場合
                            if (currentLine.Count >= 2)
                            {
                                // 現在のラインのデータの最初の点と最後の点を取得
                                var firstDataPoint = currentLine.FirstOrDefault();
                                var lastDataPoint = currentLine.LastOrDefault();

                                if (lastDataPoint.Y != newDataPoint.Y)// 前回のデータと同じ値の場合はラインの向きに変化がないものとして無視する。
                                {
                                    // これまでのラインの向き(true:上昇, false:下降)
                                    bool lastDirect = firstDataPoint.Y < lastDataPoint.Y;
                                    // 今回のラインの向きを判定する。(true:上昇, false:下降)
                                    bool lineDirect = lastDataPoint.Y < newDataPoint.Y;

                                    // 方向が変わった場合
                                    if (lineDirect != lastDirect)
                                    {
                                        // 振幅の高さが閾値を超えない場合は無呼吸と判定
                                        var hight = lastDataPoint.Y - firstDataPoint.Y;
                                        var abs = Math.Abs(hight); // 上下した振幅の幅を取得
                                        isApnea = ApneaThreshold > abs;   // 無呼吸の状態を更新
                                        Debug.WriteLine($"{time:HH:mm:ss.fff} {lastDataPoint.Y,5} Hight: {hight,5} Direct: {(lineDirect ? "up" : "down"),-4} Apnea: {isApnea}");  // コンソールに受信データを出力

                                        // 無呼吸の状態ではない場合は、ラインの向きを判定して無呼吸の判定を行う。
                                        if (!preApenaStatus)
                                        {
                                            // 無呼吸となった場合
                                            if (isApnea)
                                            {
                                                // グラフの描画をUIスレッドに依頼するため、現在のラインを退避
                                                var apneaDataLine = currentLine;

                                                // グラフの描画をUIスレッドに依頼
                                                Dispatcher.BeginInvoke(new Action(() =>
                                                {
                                                    // 新しく無呼吸のラインを追加
                                                    var apnea = new LineSeries { StrokeThickness = ApneaStrokeThickness, Color = OxyColors.Red };
                                                    PlotModel.Series.Add(apnea);

                                                    // 無呼吸グラフにデータを追加
                                                    foreach (var dataPoint in apneaDataLine)
                                                    {
                                                        apnea.Points.Add(dataPoint);
                                                    }
                                                    // グラフを再描画
                                                    PlotModel.InvalidatePlot(true);

                                                }), DispatcherPriority.Background);
                                            }
                                            else
                                            {
                                                // UIスレッドに依頼
                                                Dispatcher.BeginInvoke(new Action(() =>
                                                {
                                                    // 無呼吸のラインを終了
                                                    seriesApnea = null;
                                                }), DispatcherPriority.Background);
                                            }
                                        }

                                        // 方向が変わったので、これまでのラインの終了点を開始点として現在のラインのデータを新しく作成する。
                                        currentLine =
                                        [
                                            lastDataPoint,
                                        ];
                                    }

                                    // 無呼吸の状態の場合は、閾値超えた時に無呼吸の判定を解除する。
                                    if (isApnea)
                                    {
                                        // 開始点と現在の点の振幅が閾値を超えた場合は無呼吸の判定を解除
                                        var abs = Math.Abs(newDataPoint.Y - firstDataPoint.Y); // 上下した振幅の幅を取得
                                        isApnea = ApneaThreshold > abs;   // 無呼吸の状態を更新
                                                                          // 無呼吸の状態が解除された場合
                                        if (!isApnea)
                                        {
                                            // UIスレッドに依頼
                                            Dispatcher.BeginInvoke(new Action(() =>
                                            {
                                                // 無呼吸のラインを終了
                                                seriesApnea = null;
                                            }), DispatcherPriority.Background);
                                        }
                                    }
                                }
                            }

                            // 現在のデータを追加
                            currentLine.Add(newDataPoint);

                            // 無呼吸状態の場合は、無呼吸のグラフにもデータを追加
                            if (isApnea)
                            {
                                // UIスレッドでグラフ描画
                                Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    if (seriesApnea == null)
                                    {
                                        seriesApnea = new LineSeries { StrokeThickness = ApneaStrokeThickness, Color = OxyColors.Red };
                                        PlotModel.Series.Add(seriesApnea);
                                    }
                                    // 無呼吸グラフにデータを追加
                                    seriesApnea.Points.Add(newDataPoint);
                                    // グラフを再描画
                                    PlotModel.InvalidatePlot(true);
                                }), DispatcherPriority.Background);
                            }
                        }
                        finally
                        {
                            lastTime = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, time.Second);
                            var denominator = (int)(PointSeconds * 1000);
                            var mill = time.Millisecond / denominator;
                            if (mill > 0)
                            {
                                lastTime = lastTime.Value.AddMilliseconds(denominator * mill);
                            }
                        }
                    }
                }
            }, cancellationToken);
        }

        private void StartDeviceDetection(CancellationToken cancellationToken)
        {
            Task.Run(() =>
            {
                var interval = (int)(DeviceDetectPollingSeconds * 1000);

                while (!cancellationToken.IsCancellationRequested)
                {
                    Thread.Sleep(interval); // 1秒ごとにチェック

                    DetectDevice();
                }
            }, cancellationToken);
        }


        private void DetectDevice()
        {
            string[] ports = SerialPortStream.GetPortNames();
            foreach (var port in ports)
            {
                try
                {
                    Debug.WriteLine($"Device Detect {port}");
                    SerialPortStream serialPort = new SerialPortStream(port, 38400);
                    serialPort.WriteTimeout = (int)(SerialPortWriteTimeout * 1000);
                    serialPort.ReadTimeout = (int)(SerialPortReadTimeout * 1000);
                    serialPort.Encoding = Encoding.UTF8;
                    serialPort.NewLine = NewLine;
                    serialPort.Open();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DeviceLogs = $"{port}に接続を試みます。";
                    }), DispatcherPriority.Background);

                    try
                    {
                        string id = string.Empty;
                        string version = string.Empty;

                        // デバイス検出ロジック（応答確認など）
                        var data = serialPort.ReadExisting();
                        if (data.Length > 0)
                        {
                            Debug.WriteLine($"Received({data.Length}): {data}");  // コンソールに受信データを出力
                        }

                        id = GetDeviceId(serialPort);
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DeviceLogs = $"DEVICE ID {id}";
                        }), DispatcherPriority.Background);

                        version = GetVersion(serialPort);
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            // ここで受信データをグラフに追加するなどのUI更新を行う
                            DeviceInfo = $"ID {id} {version}";
                            DeviceLogs = $"VERSION {version}";

                            DeviceLogs = $"{port}に接続しました。";
                        }), DispatcherPriority.Background);

                        while (true)
                        {
                            DebugModeEnter(serialPort);

                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                ConnectionStatus = $"データを受信するまで待っています。";
                            }), DispatcherPriority.Background);
                            WaitForReStart(serialPort);

                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                ConnectionStatus = $"データを受信しています。";
                            }), DispatcherPriority.Background);
                            ReceivedData(serialPort);
                        }
                    }
                    catch (TimeoutException)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DeviceLogs = $"{port}との接続が切れました。";
                            ConnectionStatus = $"{port}との接続が切れました。";
                            // タイムアウト時の処理
                            UpdateConnectionStatus(false);
                        }), DispatcherPriority.Background);
                    }
                    finally
                    {
                        Debug.WriteLine($"serialPort close: {serialPort} IsOpen:{serialPort.IsOpen}");  // コンソールに受信データを出力
                        if (serialPort != null && serialPort.IsOpen)
                        {
                            serialPort.Close();
                        }
                    }
                }
                catch (Exception)
                {
                    // 接続失敗時の処理
                    Debug.WriteLine($"DetectDevice Error {port}");
                    // Debug.WriteLine(e);
                }
            }
        }

        private string GetDeviceId(SerialPortStream serialPort)
        {
            const string COMMAND = "DID";
            const string PATTERN = @"DEVICE ID ([a-zA-Z0-9]+)";

            serialPort.WriteLine(COMMAND);
            while (true)
            {
                var line = serialPort.ReadLine();
                Debug.WriteLine($"Received: {line}");  // コンソールに受信データを出力
                Match match = Regex.Match(line, PATTERN);
                if (match.Success)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // 受信を確認したことを示すため、通信状態を更新
                        UpdateConnectionStatus(true);
                    }), DispatcherPriority.Background);
                    return match.Groups[1].Value;
                }
            }
        }

        private string GetVersion(SerialPortStream serialPort)
        {
            const string COMMAND = "VERSION";
            const string PATTERN_TI = @"TI:([a-zA-Z0-9\.\-_]+)";
            const string PATTERN_ATMEL = @"Atmel:([a-zA-Z0-9\.\-_]+)";

            string ti = string.Empty;
            string atmel = string.Empty;

            serialPort.WriteLine(COMMAND);
            while (true)
            {
                var line = serialPort.ReadLine();
                Debug.WriteLine($"Received: {line}");  // コンソールに受信データを出力
                Match match = Regex.Match(line, PATTERN_TI);
                if (match.Success)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // 受信を確認したことを示すため、通信状態を更新
                        UpdateConnectionStatus(true);
                    }), DispatcherPriority.Background);
                    ti = match.Groups[1].Value;
                    break;
                }
            }

            while (true)
            {
                var line = serialPort.ReadLine();
                Debug.WriteLine($"Received: {line}");  // コンソールに受信データを出力
                Match match = Regex.Match(line, PATTERN_ATMEL);
                if (match.Success)
                {
                    atmel = match.Groups[1].Value;
                    break;
                }
            }

            return $"TI:{ti} ATMEL:{atmel}";
        }

        private void DebugModeEnter(SerialPortStream serialPort)
        {
            const string COMMAND = "DEBUG";

            while (true)
            {
                serialPort.WriteLine(COMMAND);
                while (true)
                {
                    var line = serialPort.ReadLine();
                    Debug.WriteLine($"Received: {line}");  // コンソールに受信データを出力
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DeviceLogs = $"{line}";
                        // 受信を確認したことを示すため、通信状態を更新
                        UpdateConnectionStatus(true);
                    }), DispatcherPriority.Background);

                    if (line.Contains("Debug mode Enter."))
                    {
                        return;
                    }
                    else if (line.Contains("Debug mode Exit."))
                    {
                        break;
                    }
                }
            }
        }

        private void WaitForReStart(SerialPortStream serialPort)
        {
            const string COMMAND = "ACC";
            var WAIT_MILLISECONDS = (int)(DevicePollingSeconds * 1000);

            int readTimeout = serialPort.ReadTimeout;
            while (true)
            {
                serialPort.ReadTimeout = WAIT_MILLISECONDS;
                try
                {
                    var line = serialPort.ReadLine();
                    Debug.WriteLine($"Received: {line}");  // コンソールに受信データを出力
                    if (line.Contains("SerialCommunicator start."))
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DeviceLogs = line;
                            // 受信を確認したことを示すため、通信状態を更新
                            UpdateConnectionStatus(true);
                        }), DispatcherPriority.Background);
                        break;
                    }
                    else if (line.Contains("version"))
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DeviceLogs = line;
                            // 受信を確認したことを示すため、通信状態を更新
                            UpdateConnectionStatus(true);
                        }), DispatcherPriority.Background);
                        break;
                    }
                    else if (line.Contains("Atmel:"))
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DeviceLogs = line;
                            // 受信を確認したことを示すため、通信状態を更新
                            UpdateConnectionStatus(true);
                        }), DispatcherPriority.Background);
                        break;
                    }
                    else if (line.Contains("ID :"))
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DeviceLogs = line;
                            // 受信を確認したことを示すため、通信状態を更新
                            UpdateConnectionStatus(true);
                        }), DispatcherPriority.Background);
                        break;
                    }
                    else if (line.Contains(" MB "))
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DeviceLogs = line;
                            // 受信を確認したことを示すため、通信状態を更新
                            UpdateConnectionStatus(true);
                        }), DispatcherPriority.Background);
                        break;
                    }
                    else if (line.Contains(" KB "))
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DeviceLogs = line;
                            // 受信を確認したことを示すため、通信状態を更新
                            UpdateConnectionStatus(true);
                        }), DispatcherPriority.Background);
                        break;
                    }
                    else if (line.Contains("sample "))
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DeviceLogs = line;
                            // 受信を確認したことを示すため、通信状態を更新
                            UpdateConnectionStatus(true);
                        }), DispatcherPriority.Background);
                        break;
                    }
                    else if (line.Contains("acc "))
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            DeviceLogs = line;
                            // 受信を確認したことを示すため、通信状態を更新
                            UpdateConnectionStatus(true);
                        }), DispatcherPriority.Background);
                        break;
                    }
                    else
                    {
                        const string PATTERN = @"([0-9\+\-\,]+),([0-9\+\-\,]+),([0-9\+\-\,]+)";
                        Match match = Regex.Match(line, PATTERN);
                        if (match.Success)
                        {
                            int x = int.Parse(match.Groups[1].Value);
                            int y = int.Parse(match.Groups[2].Value);
                            int z = int.Parse(match.Groups[3].Value);

                            _dataQueue.Enqueue(new Tuple<DateTime, int, int, int>(item1: DateTime.Now, x, y, z));
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                // 受信を確認したことを示すため、通信状態を更新
                                UpdateConnectionStatus(true);
                            }), DispatcherPriority.Background);
                            return;
                        }
                    }
                }
                catch (TimeoutException)
                {
                }
                finally
                {
                    serialPort.ReadTimeout = readTimeout;
                }

                GetDeviceId(serialPort);
            }

            serialPort.ReadTimeout = WAIT_MILLISECONDS;
            try
            {
                while(true)
                {
                    var line = serialPort.ReadLine();
                    Debug.WriteLine($"Received: {line}");  // コンソールに受信データを出力
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DeviceLogs = line;
                        // 受信を確認したことを示すため、通信状態を更新
                        UpdateConnectionStatus(true);
                    }), DispatcherPriority.Background);
                }
            }
            catch (TimeoutException)
            {
                // タイムアウト時の処理
            }
            finally
            {
                serialPort.ReadTimeout = readTimeout;
            }

            serialPort.WriteLine(COMMAND);
            while (true)
            {
                var line = serialPort.ReadLine();
                Debug.WriteLine($"Received: {line}");  // コンソールに受信データを出力
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    DeviceLogs = line;
                    // 受信を確認したことを示すため、通信状態を更新
                    UpdateConnectionStatus(true);
                }), DispatcherPriority.Background);

                if (line.Contains("X,Y,Z"))
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        DeviceLogs = $"ADC Start!";
                    }), DispatcherPriority.Background);
                    break;
                }
            }
        }

        public void ReceivedData(SerialPortStream serialPort)
        {
            const int WAIT_MILLISECONDS = 1000;

            const string PATTERN = @"([0-9\+\-\,]+),([0-9\+\-\,]+),([0-9\+\-\,]+)";

            int readTimeout = serialPort.ReadTimeout;
            serialPort.ReadTimeout = WAIT_MILLISECONDS;
            try
            {
                while (true)
                {
                    var line = serialPort.ReadLine();
                    var now = DateTime.Now;

                    Match match = Regex.Match(line, PATTERN);
                    if (match.Success)
                    {
                        int x = int.Parse(match.Groups[1].Value);
                        int y = int.Parse(match.Groups[2].Value);
                        int z = int.Parse(match.Groups[3].Value);
                        //Debug.WriteLine($"Received: {line} => {x}, {y}, {z}");  // コンソールに受信データを出力
                        _dataQueue.Enqueue(new Tuple<DateTime, int, int, int>(now, x, y, z));
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            // 受信を確認したことを示すため、通信状態を更新
                            UpdateConnectionStatus(true);
                        }), DispatcherPriority.Background);
                    }
                    else
                    {
                        if (line.Contains("ADC stop!."))
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                DeviceLogs = line;
                                ConnectionStatus = $"データが停止しました。";
                                // 受信を確認したことを示すため、通信状態を更新
                                UpdateConnectionStatus(true);
                            }), DispatcherPriority.Background);
                            break;
                        }
                        else
                        {
                            Debug.WriteLine($"Received: {line}");  // コンソールに受信データを出力
                        }
                    }
                }
            }
            finally
            {
                serialPort.ReadTimeout = readTimeout;
            }
        }
    }
}