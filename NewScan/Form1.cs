using Fleck;
using NTwain;
using NTwain.Data;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Timers;

namespace NewScan
{
    public partial class Form1 : Form
    {
        ImageCodecInfo _tiffCodecInfo;
        TwainSession _twain;
        bool _stopScan;
        bool _loadingCaps;
        List<IWebSocketConnection> allSockets;
        WebSocketServer server;
        System.Timers.Timer _timer =  new System.Timers.Timer(180000);
        string _defaultScan;
        string _path = @"config.ini";
        public Form1()
        {
            InitializeComponent();

            comboQuality.SelectedIndex = 1;

            if (NTwain.PlatformInfo.Current.IsApp64Bit)
            {
                Text = Text + " (64bit)";
            }
            else
            {
                Text = Text + " (32bit)";
            }
            foreach (var enc in ImageCodecInfo.GetImageEncoders())
            {
                if (enc.MimeType == "image/tiff") { _tiffCodecInfo = enc; break; }
            }

            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            //notifyIcon1.Visible = true;


            _timer.Elapsed += OnTimedEvent;
            _timer.AutoReset = true;
            _timer.Enabled = true;


            allSockets = new List<IWebSocketConnection>();
            server = new WebSocketServer("ws://0.0.0.0:3000");
            server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    Console.WriteLine("Open!");
                    allSockets.Add(socket);
                };
                socket.OnClose = () =>
                {
                    Console.WriteLine("Close!");
                    allSockets.Remove(socket);
                };
                socket.OnMessage = message =>
                {
                    if (message == "1100")
                    {
                        Invoke(new Action(()=> {
                            this.Show();                            
                            WindowState = FormWindowState.Minimized;
                            WindowState = FormWindowState.Normal;                          
                          
                        }));
                        reconnect();
                    }

                    if (message == "close")
                    {
                        CleanupTwain();
                    }
                    if (message == "timer")
                    {
                        _timer.Stop();
                        _timer.Start();
                    }

                };
            });

            getFromFile();

        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetupTwain();

        }


        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
            if (_twain != null)
            {
                if (e.CloseReason == CloseReason.UserClosing && _twain.State > 4)
                {
                    e.Cancel = true;
                }
                else
                {
                    CleanupTwain();
                }
            }
            base.OnFormClosing(e);
        }

        private void SetupTwain()
        {
            var appId = TWIdentity.CreateFromAssembly(DataGroups.Image, Assembly.GetEntryAssembly());
            _twain = new TwainSession(appId);
            _twain.StateChanged += (s, e) =>
            {
                PlatformInfo.Current.Log.Info("State changed to " + _twain.State + " on thread " + Thread.CurrentThread.ManagedThreadId);
            };
            _twain.TransferError += (s, e) =>
            {
                PlatformInfo.Current.Log.Info("Got xfer error on thread " + Thread.CurrentThread.ManagedThreadId);
            };
            _twain.DataTransferred += (s, e) =>
            {
                PlatformInfo.Current.Log.Info("Transferred data event on thread " + Thread.CurrentThread.ManagedThreadId);

                // example on getting ext image info
                var infos = e.GetExtImageInfo(ExtendedImageInfo.Camera).Where(it => it.ReturnCode == ReturnCode.Success);
                foreach (var it in infos)
                {
                    var values = it.ReadValues();
                    PlatformInfo.Current.Log.Info(string.Format("{0} = {1}", it.InfoID, values.FirstOrDefault()));
                    break;
                }

                Image img = null;
                if (e.NativeData != IntPtr.Zero)
                {
                    var stream = e.GetNativeImageStream();
                    if (stream != null)
                    {
                        long quality = (comboQuality.SelectedIndex == 1) ? 15L : comboQuality.SelectedIndex == 0 ? 50L : 8L;
                        var  compressedBmp = GetCompressedBitmap(new Bitmap(stream), quality);
                        
                        //var outPut = StreamToByte(stream);
                        var outPut = StreamToByte(compressedBmp);
                        foreach (var socket in allSockets.ToList())
                        {
                            socket.Send(outPut);
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(e.FileDataPath))
                {
                    img = new Bitmap(e.FileDataPath);                    
                }

            };
            _twain.SourceDisabled += (s, e) =>
            {
                PlatformInfo.Current.Log.Info("Source disabled event on thread " + Thread.CurrentThread.ManagedThreadId);
                this.BeginInvoke(new Action(() =>
                {
                    ButtonScan.Enabled = true;
                    btnAllSettings.Enabled = true;
                    LoadSourceCaps();
                }));
            };
            _twain.TransferReady += (s, e) =>
            {
                PlatformInfo.Current.Log.Info("Transferr ready event on thread " + Thread.CurrentThread.ManagedThreadId);
                e.CancelAll = _stopScan;
            };

            // either set sync context and don't worry about threads during events,
            // or don't and use control.invoke during the events yourself
            PlatformInfo.Current.PreferNewDSM = false;
            PlatformInfo.Current.Log.Info("Setup thread = " + Thread.CurrentThread.ManagedThreadId);
            _twain.SynchronizationContext = SynchronizationContext.Current;
            if (_twain.State < 3)
            {
                // use this for internal msg loop
                _twain.Open();
                ReloadSourceList();
                // use this to hook into current app loop
                //_twain.Open(new WindowsFormsMessageLoopHook(this.Handle));
            }
           
        }




        public static byte[] ImageToByte(Image img)
        {
            using (var stream = new MemoryStream())
            {
                img.Save(stream, ImageFormat.Jpeg);                

                return stream.ToArray();
            }
        }

        private MemoryStream GetCompressedBitmap(Bitmap bmp, long quality)
        {
            using (var mss = new MemoryStream())
            {
                EncoderParameter qualityParam = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                ImageCodecInfo imageCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(o => o.FormatID == ImageFormat.Jpeg.Guid);
                EncoderParameters parameters = new EncoderParameters(1);
                parameters.Param[0] = qualityParam;
                bmp.Save(mss, imageCodec, parameters);
                return mss;
            }
        }
    

        private void ClearResourse()
        {
            Invoke(new Action(() =>
            {               
                comboDepth.DataSource = null;
                comboDepth.Items.Clear();
                comboDepth.SelectedIndex = -1;                
                btnAllSettings.Enabled = false;
                ButtonScan.Text = "Подключиться";
                foreach (var btn in btnSources.DropDownItems)
                {
                    var srcBtn = btn as ToolStripMenuItem;
                    if (srcBtn != null)
                    {
                        srcBtn.Checked = false;

                    }
                }                
            }));

        }


        private void CleanupTwain()
        {
            if (_twain.State == 4)
            {
                _twain.CurrentSource.Close();

            }
            if (_twain.State == 3)
            {
                _twain.Close();
            }

            if (_twain.State > 2)
            {
                // normal close down didn't work, do hard kill
                _twain.ForceStepDown(2);
            }

            ClearResourse();
        }

        private void OnTimedEvent(Object source, ElapsedEventArgs e)
        {
            _timer.Stop();
            CleanupTwain();
        }



        #region toolbar

        private void btnSources_DropDownOpening(object sender, EventArgs e)
        {
            if (btnSources.DropDownItems.Count == 2)
            {
                ReloadSourceList();
            }
        }

        private void reloadSourcesListToolStripMenuItem_Click(object sender, EventArgs e)
        {
            _timer.Stop();
            _timer.Start();

            ReloadSourceList();
        }

        private void ReloadSourceList()
        {
            if (_twain.State >= 3)
            {
                while (btnSources.DropDownItems.IndexOf(sepSourceList) > 0)
                {
                    var first = btnSources.DropDownItems[0];
                    first.Click -= SourceMenuItem_Click;
                    btnSources.DropDownItems.Remove(first);
                }
                foreach (var src in _twain)
                {
                    var srcBtn = new ToolStripMenuItem(src.Name);
                    srcBtn.Tag = src;
                    srcBtn.Click += SourceMenuItem_Click;
                    srcBtn.Checked = _twain.CurrentSource != null && _twain.CurrentSource.Name == src.Name;
                    btnSources.DropDownItems.Insert(0, srcBtn);
                    if (src.Name == _defaultScan) //Сомнительный кусок кода выбор сканера
                    {
                        if (src.Open() == ReturnCode.Success)
                        {
                            srcBtn.Checked = true;
                            ButtonScan.Enabled = true;
                            btnAllSettings.Enabled = true;
                            LoadSourceCaps();
                        }
                    }
                }
            }
        }

        void SourceMenuItem_Click(object sender, EventArgs e)
        {

            if (_twain.State > 4) { return; }

            if (_twain.State == 4) { _twain.CurrentSource.Close(); }

            if (_twain.State < 3)
            {
                _twain.Open();
            }

            foreach (var btn in btnSources.DropDownItems)
            {
                var srcBtn = btn as ToolStripMenuItem;
                if (srcBtn != null) { srcBtn.Checked = false; }
            }

            var curBtn = (sender as ToolStripMenuItem);
            var src = curBtn.Tag as DataSource;


            if (src.Open() == ReturnCode.Success)
            {                
                curBtn.Checked = true;                               
                ButtonScan.Enabled = true;
                btnAllSettings.Enabled = true;
                LoadSourceCaps();
                _defaultScan = src.Name;
                setToFile();
            }
        }

        private void btnStartCapture_Click(object sender, EventArgs e)
        {

        }

        private void btnStopScan_Click(object sender, EventArgs e)
        {
        
        }

        #endregion

        #region cap control


        private void LoadSourceCaps()
        {
            var src = _twain.CurrentSource;
            _loadingCaps = true;

            //var test = src.SupportedCaps;

            if (src.Capabilities.ICapPixelType.IsSupported)
            {
                LoadDepth(src.Capabilities.ICapPixelType);
            }
            if (src.Capabilities.ICapXResolution.IsSupported && src.Capabilities.ICapYResolution.IsSupported)
            {
                _twain.CurrentSource.Capabilities.ICapXResolution.SetValue(300);
                _twain.CurrentSource.Capabilities.ICapYResolution.SetValue(300);
                //LoadDPI(src.Capabilities.ICapXResolution);
            }
            // TODO: find out if this is how duplex works or also needs the other option
            //if (src.Capabilities.CapDuplexEnabled.IsSupported)
            //{
            //    LoadDuplex(src.Capabilities.CapDuplexEnabled);
            //}
            //if (src.Capabilities.ICapSupportedSizes.IsSupported)
            //{
            //    LoadPaperSize(src.Capabilities.ICapSupportedSizes);
            //}
            btnAllSettings.Enabled = src.Capabilities.CapEnableDSUIOnly.IsSupported;
            _loadingCaps = false;
        }

        private void LoadPaperSize(ICapWrapper<SupportedSize> cap)
        {
            var list = cap.GetValues().ToList();
            //comboSize.DataSource = list;
            var cur = cap.GetCurrent();
            if (list.Contains(cur))
            {
                //comboSize.SelectedItem = cur;
            }

        }


        private void LoadDuplex(ICapWrapper<BoolType> cap)
        {
            //ckDuplex.Checked = cap.GetCurrent() == BoolType.True;
        }


        private void LoadDPI(ICapWrapper<TWFix32> cap)
        {
            // only allow dpi of certain values for those source that lists everything
            var list = cap.GetValues().Where(dpi => (dpi % 50) == 0).ToList();
            //comboDPI.DataSource = list;
            var cur = cap.GetCurrent();
            if (list.Contains(cur))
            {
                //comboDPI.SelectedItem = cur;
            }
        }

        private void LoadDepth(ICapWrapper<PixelType> cap)
        {            
            var list = cap.GetValues().ToList();
            comboDepth.DataSource = list;
            var cur = cap.GetCurrent();
            if (list.Contains(cur))
            {
                comboDepth.SelectedItem = cur;
            }
        }

        private void comboSize_SelectedIndexChanged(object sender, EventArgs e)
        {
            
        }

        private void comboDepth_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!_loadingCaps && _twain.State == 4)
            {
                var sel = (PixelType)comboDepth.SelectedItem;                
                _twain.CurrentSource.Capabilities.ICapPixelType.SetValue(sel);
            }
        }

        private void comboDPI_SelectedIndexChanged(object sender, EventArgs e)
        {
            
        }

        private void ckDuplex_CheckedChanged(object sender, EventArgs e)
        {
            
        }

        private void btnAllSettings_Click(object sender, EventArgs e)
        {
            _twain.CurrentSource.Enable(SourceEnableMode.ShowUIOnly, true, this.Handle);
        }

        #endregion
        public static byte[] StreamToByte(Stream input)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                input.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public static byte[] StreamToByte(MemoryStream input)
        {
            //using (MemoryStream ms = new MemoryStream())
            //{
            //    input.CopyTo(ms);
            //    return ms.ToArray();
            //}
            return input.ToArray();
        }

        private void groupDepth_Enter(object sender, EventArgs e)
        {

        }

        private void panelOptions_Paint(object sender, PaintEventArgs e)
        {

        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.WindowState = FormWindowState.Minimized;
                this.ShowInTaskbar = false;
                this.ShowIcon = false;
                notifyIcon1.Visible = true;
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void Form1_Resize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized)
            {
                this.ShowIcon = false;
                notifyIcon1.Visible = true;
                this.ShowInTaskbar = false;
                //notifyIcon1.ShowBalloonTip(100);
                this.Hide();
             
            }      
               
        }


        private bool reconnect()
        {
            _timer.Stop();
            _timer.Start();
            if (ButtonScan.Text == "Подключиться")
            {
                if (_twain.State > 4) { return false; }

                if (_twain.State == 4) { _twain.CurrentSource.Close(); }

                if (_twain.State < 3)
                {
                    _twain.Open();
                }
                Invoke(new Action(() =>
                {
                foreach (var src in _twain)
                {
                    if (src.Name == _defaultScan) 
                    {
                        if (src.Open() == ReturnCode.Success)
                        {                            
                            ButtonScan.Enabled = true;
                            btnAllSettings.Enabled = true;
                            LoadSourceCaps();
                        }
                    }
                }
              
                    foreach (var btn in btnSources.DropDownItems)
                    {
                        var strbtn = btn as ToolStripMenuItem;
                        if (strbtn != null)
                            if (strbtn.Text == _defaultScan)
                            {
                                strbtn.Checked = true;
                                break;
                            }
                    }
                

                ButtonScan.Text = "Начать сканирование";
                }));
                return false;
            }
            return true;
        }

        private void ButtonScan_Click(object sender, EventArgs e)
        {

            if (reconnect()) 
            

            if (_twain.State == 4)
            {
                //_twain.CurrentSource.CapXferCount.Set(4);

                _stopScan = false;

                if (_twain.CurrentSource.Capabilities.CapUIControllable.IsSupported)//.SupportedCaps.Contains(CapabilityId.CapUIControllable))
                {
                    // hide scanner ui if possible
                    if (_twain.CurrentSource.Enable(SourceEnableMode.NoUI, false, this.Handle) == ReturnCode.Success)
                    {                       
                        ButtonScan.Enabled = false;
                        btnAllSettings.Enabled = false;
                        this.WindowState = FormWindowState.Minimized;
                    }
                }
                else
                {
                    if (_twain.CurrentSource.Enable(SourceEnableMode.ShowUI, true, this.Handle) == ReturnCode.Success)
                    {
                        ButtonScan.Enabled = false;
                        btnAllSettings.Enabled = false;
                        this.WindowState = FormWindowState.Minimized;
                    }
                }
            }
        }
        private void getFromFile()
        {
            if (File.Exists(_path))
            {
                using (var sr = new StreamReader(_path))
                {
                    _defaultScan = sr.ReadLine();
                    sr.Close();
                }
            }
            else
                _defaultScan = "";
        }

        private void setToFile()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            using (var sr = new StreamWriter(_path))
            {
                sr.WriteLine(_defaultScan);
                sr.Close();
            }
        }
    }
}
