using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using RJCP.IO.Ports;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace apnea_demo
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
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

        public double PointSeconds { get; set; } = 0.5;

        public double MovingAveragePeriod { get; set; } = 1.2;

        public int ApneaThreshold { get; set; } = 100;

        public PlotModel PlotModel { get; set; }

        public int AxisSecondsMaximum { get; set; } = 60;

        public int AxisSecondsMinimum { get; set; } = -240;

        public int AxisValueMaximum { get; set; } = 5000;

        public int AxisValueMinimum { get; set; } = -2000;

        private bool _autoScroll = true;
        public bool IsAutoScroll
        {
            get { return _autoScroll; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private ConcurrentQueue<Tuple<DateTime, int, int, int>> _dataQueue = new ConcurrentQueue<Tuple<DateTime, int, int, int>>();


        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
            });
            // Y軸の設定
            PlotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = "Value",
                Minimum = AxisValueMinimum,
                Maximum = AxisValueMaximum
            });

            _deviceInfo = string.Empty;

            DataContext = this;

            Loaded += MainWindow_Loaded;
            Unloaded += MainWindow_Unloaded;

            _cancellationTokenSource = new CancellationTokenSource();
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
                            (DateTime time, int value) coordinate;
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

                                var val = sum / movingAverageValues.Count;
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
                                    seriesBase = new LineSeries { Color = OxyColors.Black };
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
                                                    var apnea = new LineSeries { StrokeThickness = 3, Color = OxyColors.Red };
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
                                        seriesApnea = new LineSeries { StrokeThickness = 4, Color = OxyColors.Red };
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
                while (!cancellationToken.IsCancellationRequested)
                {
                    Thread.Sleep(1000); // 1秒ごとにチェック

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
                    serialPort.WriteTimeout = 500;
                    serialPort.ReadTimeout = 500;
                    serialPort.Encoding = Encoding.UTF8;
                    serialPort.NewLine = "\r\n";
                    serialPort.Open();

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
                        version = GetVersion(serialPort);

                        Dispatcher.Invoke(() =>
                        {
                            // ここで受信データをグラフに追加するなどのUI更新を行う
                            DeviceInfo = $"ID {id} {version}";
                        });

                        while (true)
                        {
                            DebugModeEnter(serialPort);

                            WaitForReStart(serialPort);

                            ReceivedData(serialPort);
                        }
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

            try
            {
                while (true)
                {
                    serialPort.WriteLine(COMMAND);
                    while (true)
                    {
                        var line = serialPort.ReadLine();
                        Debug.WriteLine($"Received: {line}");  // コンソールに受信データを出力
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
            catch (TimeoutException)
            {
                // タイムアウト時の処理
            }
        }

        private void WaitForReStart(SerialPortStream serialPort)
        {
            const string COMMAND = "ACC";

            int readTimeout = serialPort.ReadTimeout;
            serialPort.ReadTimeout = -1;
            try
            {
                while (true)
                {
                    var line = serialPort.ReadLine();
                    Debug.WriteLine($"Received: {line}");  // コンソールに受信データを出力
                    if (line.Contains("SerialCommunicator start."))
                    {
                        break;
                    }
                    else if (line.Contains("version"))
                    {
                        continue;
                    }
                    else if (line.Contains("Atmel:"))
                    {
                        continue;
                    }
                    else if (line.Contains("ID :"))
                    {
                        continue;
                    }
                    else if (line.Contains(" MB "))
                    {
                        continue;
                    }
                    else if (line.Contains(" KB "))
                    {
                        continue;
                    }
                    else if (line.Contains("sample "))
                    {
                        continue;
                    }
                    else if (line.Contains("acc "))
                    {
                        continue;
                    }
                    else if (line.Contains("ACC"))
                    {
                        continue;
                    }
                    else if (line.Contains("X,Y,Z"))
                    {
                        return;
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
                            return;
                        }
                    }
                }

                serialPort.ReadTimeout = 1000;
                try
                {
                    while (true)
                    {
                        var line = serialPort.ReadLine();
                        Debug.WriteLine($"Received: {line}");  // コンソールに受信データを出力
                    }
                }
                catch (TimeoutException)
                {
                    // タイムアウト時の処理
                }
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
                if (line.Contains("X,Y,Z"))
                {
                    break;
                }
            }
        }

        public void ReceivedData(SerialPortStream serialPort)
        {
            const string PATTERN = @"([0-9\+\-\,]+),([0-9\+\-\,]+),([0-9\+\-\,]+)";

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
                    }
                    else
                    {
                        Debug.WriteLine($"Received: {line}");  // コンソールに受信データを出力
                    }
                }
            }
            catch (TimeoutException)
            {
                // タイムアウト時の処理
            }
        }
    }
}