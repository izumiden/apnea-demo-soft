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
using System.Windows.Shapes;
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

        public PlotModel PlotModel { get; set; }

        public int AxisSecondsMaximum { get; set; } = 10;

        public int AxisSecondsMinimum { get; set; } = -120;

        public int AxisValueMaximum { get; set; } = 8000;

        public int AxisValueMinimum { get; set; } = -8000;


        public int MovingAveragePeriod { get; set; } = 10;

        public int ApneaThreshold { get; set; } = 1000;

        public int NumberOfStandingValues { get; set; } = 25;

        private bool _autoScroll = true;
        public bool IsAutoScroll
        {
            get { return _autoScroll; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private ConcurrentQueue<Tuple<int, int, int>> _dataQueue = new ConcurrentQueue<Tuple<int, int, int>>();


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
                IntervalLength = 60000,  // 軸の間隔のピクセル数
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

                // 移動平均の値を保持するリスト
                var movingAverageValues = new List<int>();
                // 現在のラインのデータを保持するリスト
                var currentLineDataPoints = new List<DataPoint>();

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_dataQueue.TryDequeue(out var data))
                    {
                        try
                        {
                            var now = DateTime.Now;
                            var time = now.ToOADate();
                            var timeMin = now.AddSeconds(AxisSecondsMinimum).ToOADate();
                            var timeMax = now.AddSeconds(AxisSecondsMaximum).ToOADate();

                            int x = data.Item1;
                            int y = data.Item2;
                            int z = data.Item3;
                            int sum = 0;
                            int val = 0;

                            // 無呼吸状態を退避
                            bool finalApenaStatus = isApnea;

                            // 移動平均の値のリストに追加
                            movingAverageValues.Add(y);
                            if (movingAverageValues.Count > MovingAveragePeriod)
                            {
                                // 件数を超える場合は、最初の値を移動平均の値のリストから削除
                                movingAverageValues.RemoveAt(0);
                            }

                            int valueCount = 0;
                            int preValue = 0;
                            bool first = true;
                            foreach (var value in movingAverageValues)
                            {
                                sum += value;
                                if (first)
                                {
                                    first = false;
                                }
                                else
                                {
                                    if (preValue != value)
                                    {
                                        valueCount++;
                                    }
                                }
                                preValue = value;
                            }

                            if (valueCount < 2)
                            {
                                // 現在描画中のグラフがある場合は、グラフの参照を削除
                                if (seriesBase != null || seriesApnea != null)
                                {
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        seriesBase = null;
                                        seriesApnea = null;
                                    }), DispatcherPriority.Background);
                                }
                                // 2回以上の値が変化していない場合は、無視する。
                                continue;
                            }

                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                if (seriesBase == null)
                                {
                                    seriesBase = new LineSeries { Color = OxyColors.Black };
                                    PlotModel.Series.Add(seriesBase);
                                }
                            }), DispatcherPriority.Background);

                            val = sum / movingAverageValues.Count;

                            // 移動平均の値で追加するDataPointを作成
                            var newDataPoint = new DataPoint(time, val);

                            // UIスレッドでグラフ描画
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                // グラフにデータを追加
                                seriesBase?.Points.Add(newDataPoint);
                                // 無呼吸状態の場合は、無呼吸のグラフにもデータを追加
                                seriesApnea?.Points.Add(newDataPoint);

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

                                // グラフを再描画
                                PlotModel.InvalidatePlot(true);
                            }), DispatcherPriority.Background);


                            // ----------------------------------------------------
                            // 無呼吸の判定ロジック
                            // ----------------------------------------------------
                            // 現在のラインのデータが2点以上の場合
                            if (currentLineDataPoints.Count >= 2)
                            {
                                // 現在のラインのデータの最初の点と最後の点を取得
                                var firstDataPoint = currentLineDataPoints.FirstOrDefault();
                                var lastDataPoint = currentLineDataPoints.LastOrDefault();

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
                                        Debug.WriteLine($"Hight: {hight,5} Direct: {lineDirect,-5} Abs:{abs,4} Apnea: {isApnea}");  // コンソールに受信データを出力

                                        // 無呼吸の状態ではない場合は、ラインの向きを判定して無呼吸の判定を行う。
                                        if (!finalApenaStatus)
                                        {
                                            // 無呼吸となった場合
                                            if (isApnea)
                                            {
                                                // グラフの描画をUIスレッドに依頼するため、現在のラインを退避
                                                var apneaDataPoints = currentLineDataPoints;
                                                // 今回のデータを無呼吸として描画するために追加する。
                                                apneaDataPoints.Add(newDataPoint);


                                                // グラフの描画をUIスレッドに依頼
                                                Dispatcher.BeginInvoke(new Action(() =>
                                                {
                                                    // 新しく無呼吸のラインを追加
                                                    seriesApnea = new LineSeries { Color = OxyColors.Red };
                                                    PlotModel.Series.Add(seriesApnea);

                                                    // 無呼吸グラフにデータを追加
                                                    foreach (var dataPoint in apneaDataPoints)
                                                    {
                                                        seriesApnea.Points.Add(dataPoint);
                                                    }
                                                    // グラフを再描画
                                                    PlotModel.InvalidatePlot(true);
                                                }), DispatcherPriority.Background);
                                            }
                                        }

                                        // 方向が変わったので、これまでのラインの終了点を開始点として現在のラインのデータを新しく作成する。
                                        currentLineDataPoints =
                                        [
                                            lastDataPoint,
                                        ];
                                    }
                                    else
                                    {
                                        // 方向が変わっっていない場合
                                        // 無呼吸の状態の場合は、閾値超えた時に無呼吸の判定を解除する。
                                        if (finalApenaStatus)
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
                            }
                            // 現在のデータを追加
                            currentLineDataPoints.Add(newDataPoint);
                        }
                        finally
                        {
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
                catch (Exception e)
                {
                    // 接続失敗時の処理
                    Debug.WriteLine($"DetectDevice Error {port}");
                    Debug.WriteLine(e);
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
                    Match match = Regex.Match(line, PATTERN);
                    if (match.Success)
                    {
                        int x = int.Parse(match.Groups[1].Value);
                        int y = int.Parse(match.Groups[2].Value);
                        int z = int.Parse(match.Groups[3].Value);
                        //Debug.WriteLine($"Received: {line} => {x}, {y}, {z}");  // コンソールに受信データを出力
                        _dataQueue.Enqueue(new Tuple<int, int, int>(x, y, z));
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