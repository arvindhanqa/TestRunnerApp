using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TestRunnerApp
{
    public class LogEntry { public string Time, Type, Message, ScreenshotPath; public int Depth; public bool IsError; public List<LogEntry> Children = new List<LogEntry>(); }
    public class TestInfo { public string Name, Dll, Module, Desc, Status="Not Run", Duration, LogFile; public bool Failed; }

    public partial class MainWindow : Window
    {
        AppSettings S;
        List<TestInfo> _tests = new List<TestInfo>();
        HashSet<string> _sel = new HashSet<string>();
        List<string> _modules = new List<string>();
        List<string> _foundDlls = new List<string>();
        HashSet<string> _chkDlls = new HashSet<string>(), _loadedDlls = new HashSet<string>();
        FileSystemWatcher _watcher; CancellationTokenSource _cts; Process _proc; bool _running;
        // Cached parsed logs per test
        Dictionary<string, List<LogEntry>> _logCache = new Dictionary<string, List<LogEntry>>();
        List<string> _runFiles = new List<string>(); string _ssPath;

        static SolidColorBrush BR(string h) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
        static readonly SolidColorBrush cFg=BR("#FF333333"),cFg2=BR("#FF666666"),cFg3=BR("#FF999999"),
            cAcc=BR("#FF007ACC"),cAccL=BR("#FFB5D7EF"),cBdr=BR("#FFD2D4D7"),
            cErr=BR("#FFE53935"),cOk=BR("#FF28A745"),cWarn=BR("#FFFFC107"),cMod=BR("#FF007ACC"),
            cHover=BR("#FFF0F0F0"),cSel=BR("#FFB5D7EF"),cSelBdr=BR("#FF8EC8E8"),
            cTc=BR("#FF5C6BC0"),cStep=BR("#FF1E88E5"),cOp=BR("#FF78909C"),cSS=BR("#FF26A69A"),cInfo=BR("#FFFB8C00");

        public MainWindow()
        {
            try
            {
                InitializeComponent();
                S = AppSettings.Load();
                LoadS();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Startup error:\n{ex.Message}\n\n{ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                throw;
            }
        }

        // ═══ SETTINGS ═══
        void LoadS()
        {
            txtUrl.Text=S.SiteUrl;txtLogin.Text=S.Login;txtPwd.Text=S.Password;txtLang.Text=S.Language;txtCmp.Text=S.CompanyId;txtRm.Text=S.RmHost;txtVer.Text=S.AcumaticaVersion;
            txtSrv.Text=S.DbServer;txtDb.Text=S.DbName;txtBak.Text=S.BackupPath;txtBakOut.Text=S.PostRunBackupFolder;cmbBak.SelectedIndex=(int)S.BackupMode;
            txtBrowser.Text=S.BrowserBin;chkHead.IsChecked=S.Headless;txtExe.Text=S.TestExePath;
            txtLogDir.Text=S.LogFolder;txtPicDir.Text=S.ScreenshotFolder;txtArcDir.Text=S.LogArchiveFolder;
            chkDelLog.IsChecked=S.DeleteOldLogs;chkSound.IsChecked=S.PlaySound;
            foreach(ComboBoxItem i in cmbLog.Items)if(i.Content.ToString()==S.LogLevel){cmbLog.SelectedItem=i;break;}
            foreach(ComboBoxItem i in cmbSS.Items)if(i.Content.ToString()==S.ScreenshotActive){cmbSS.SelectedItem=i;break;}
            if(S.WindowWidth>100)Width=S.WindowWidth;if(S.WindowHeight>100)Height=S.WindowHeight;
            if(S.LastLoadedDlls?.Count>0){_foundDlls=S.LastLoadedDlls.Where(File.Exists).ToList();_chkDlls=new HashSet<string>(_foundDlls);RefreshDlls();}
        }
        void SaveS()
        {
            S.SiteUrl=txtUrl.Text;S.Login=txtLogin.Text;S.Password=txtPwd.Text;S.Language=txtLang.Text;S.CompanyId=txtCmp.Text;S.RmHost=txtRm.Text;S.AcumaticaVersion=txtVer.Text;
            S.DbServer=txtSrv.Text;S.DbName=txtDb.Text;S.BackupPath=txtBak.Text;S.PostRunBackupFolder=txtBakOut.Text;S.BackupMode=(BackupMode)cmbBak.SelectedIndex;
            S.BrowserBin=txtBrowser.Text;S.Headless=chkHead.IsChecked==true;S.TestExePath=txtExe.Text;
            S.LogFolder=txtLogDir.Text;S.ScreenshotFolder=txtPicDir.Text;S.LogArchiveFolder=txtArcDir.Text;
            S.DeleteOldLogs=chkDelLog.IsChecked==true;S.PlaySound=chkSound.IsChecked==true;
            S.LogLevel=(cmbLog.SelectedItem as ComboBoxItem)?.Content?.ToString()??"DEBUG";
            S.ScreenshotActive=(cmbSS.SelectedItem as ComboBoxItem)?.Content?.ToString()??"true";
            S.WindowWidth=ActualWidth;S.WindowHeight=ActualHeight;S.LastLoadedDlls=_loadedDlls.ToList();S.Save();
        }
        void Window_Closing(object s,System.ComponentModel.CancelEventArgs e){_watcher?.Dispose();_cts?.Cancel();SaveS();}

        // ═══ BROWSE ═══
        void Browse_Click(object s,RoutedEventArgs e){switch((s as Button)?.Tag?.ToString()){case"bak":BrF(txtBak,"BAK|*.bak|All|*.*");break;case"bakout":BrD(txtBakOut);break;case"browser":BrF(txtBrowser,"EXE|*.exe");break;case"exe":BrF(txtExe,"EXE/DLL|*.exe;*.dll");break;case"logdir":BrD(txtLogDir);break;case"picdir":BrD(txtPicDir);break;case"arcdir":BrD(txtArcDir);break;}}
        void BrF(TextBox t,string f){var d=new OpenFileDialog{Filter=f};var dir=Path.GetDirectoryName(t.Text);if(Directory.Exists(dir))d.InitialDirectory=dir;if(d.ShowDialog()==true)t.Text=d.FileName;}
        void BrD(TextBox t){var d=new OpenFileDialog{CheckFileExists=false,FileName="Select Folder",Filter="Folder|*.none"};if(Directory.Exists(t.Text))d.InitialDirectory=t.Text;if(d.ShowDialog()==true)t.Text=Path.GetDirectoryName(d.FileName);}

        async void BtnRestore_Click(object s,RoutedEventArgs e)
        {
            var r=MessageBox.Show($"Restore from:\n{txtBak.Text}\n\nNo to pick another.","Restore",MessageBoxButton.YesNoCancel);string bak=null;
            if(r==MessageBoxResult.Yes)bak=txtBak.Text;else if(r==MessageBoxResult.No){var d=new OpenFileDialog{Filter="BAK|*.bak",InitialDirectory=txtBakOut.Text};if(d.ShowDialog()==true)bak=d.FileName;else return;}else return;
            if(!File.Exists(bak)){MessageBox.Show("Not found.");return;}
            try{await RestoreDb(bak,CancellationToken.None);MessageBox.Show("Restored!");}catch(Exception ex){MessageBox.Show($"Failed: {ex.Message}");}
        }

        // ═══ DLLs ═══
        void BtnScan_Click(object s,RoutedEventArgs e)
        {
            string dir=Path.GetDirectoryName(txtExe.Text.Trim());
            if(string.IsNullOrEmpty(dir)||!Directory.Exists(dir)){var d=new OpenFileDialog{Filter="EXE|*.exe"};if(d.ShowDialog()==true){dir=Path.GetDirectoryName(d.FileName);txtExe.Text=d.FileName;}else return;}
            _foundDlls=Directory.GetFiles(dir,"Tests*.dll").Concat(Directory.GetFiles(dir,"Test*.dll")).Where(f=>!Path.GetFileName(f).Equals("TestStack.White.dll",StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Path.GetFileName).ToList();
            RefreshDlls();txtDllStat.Text=$"Found {_foundDlls.Count} DLL(s)";txtDllStat.Foreground=cOk;
            _watcher?.Dispose();_watcher=new FileSystemWatcher(dir,"*.dll"){EnableRaisingEvents=true,NotifyFilter=NotifyFilters.LastWrite|NotifyFilters.Size};
            _watcher.Changed+=(a,b)=>Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>badgeDll.Visibility=Visibility.Visible));
        }
        void BtnAddDll_Click(object s,RoutedEventArgs e){var d=new OpenFileDialog{Filter="DLL|*.dll",InitialDirectory=Path.GetDirectoryName(txtExe.Text)??@"C:\"};if(d.ShowDialog()!=true)return;if(!_foundDlls.Any(x=>x.Equals(d.FileName,StringComparison.OrdinalIgnoreCase))){_foundDlls.Add(d.FileName);_chkDlls.Add(d.FileName);RefreshDlls();}}

        void RefreshDlls()
        {
            pnlDlls.Children.Clear();
            foreach(var dp in _foundDlls){string nm=Path.GetFileNameWithoutExtension(dp).Replace("Tests","").Replace("Test","");if(string.IsNullOrEmpty(nm))nm=Path.GetFileNameWithoutExtension(dp);
            bool ck=_chkDlls.Contains(dp),ld=_loadedDlls.Contains(dp);
            var bd=new Border{Background=ck?cSel:Brushes.White,BorderBrush=ck?cSelBdr:cBdr,BorderThickness=new Thickness(0,0,0,1),Padding=new Thickness(8,5,8,5),Cursor=System.Windows.Input.Cursors.Hand};
            var st=new StackPanel{Orientation=Orientation.Horizontal};
            st.Children.Add(new CheckBox{IsChecked=ck,IsHitTestVisible=false,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,6,0)});
            st.Children.Add(new TextBlock{Text=nm,Foreground=cFg,FontSize=12,VerticalAlignment=VerticalAlignment.Center});
            if(ld)st.Children.Add(new TextBlock{Text=" ✓",Foreground=cOk,FontSize=10,VerticalAlignment=VerticalAlignment.Center});
            bd.Child=st;string p=dp;
            bd.MouseLeftButtonUp+=(a,b)=>{if(_chkDlls.Contains(p))_chkDlls.Remove(p);else _chkDlls.Add(p);RefreshDlls();};
            bd.MouseEnter+=(a,b)=>{if(!_chkDlls.Contains(p))bd.Background=cHover;};bd.MouseLeave+=(a,b)=>{bd.Background=_chkDlls.Contains(p)?cSel:Brushes.White;};
            pnlDlls.Children.Add(bd);}
        }

        // ═══ LOAD DLLs ═══
        async void BtnLoad_Click(object s,RoutedEventArgs e)
        {
            var dlls=_chkDlls.ToList();if(dlls.Count==0){txtDllStat.Text="Check at least one.";txtDllStat.Foreground=cErr;return;}
            btnLoad.IsEnabled=false;btnLoad.Content="Loading...";string asmDir=Path.GetDirectoryName(txtExe.Text.Trim());
            try{var r=await Task.Run(()=>{
                var ts=new List<TestInfo>();var mods=new HashSet<string>();var ld=new HashSet<string>();
                ResolveEventHandler rv=(a,b)=>{if(asmDir==null)return null;var d=Path.Combine(asmDir,new AssemblyName(b.Name).Name+".dll");return File.Exists(d)?Assembly.LoadFrom(d):null;};
                AppDomain.CurrentDomain.AssemblyResolve+=rv;
                foreach(var dll in dlls){try{var asm=Assembly.LoadFrom(dll);Type[] types;try{types=asm.GetTypes();}catch(ReflectionTypeLoadException ex){types=ex.Types.Where(t=>t!=null).ToArray();}
                string dllShort=Path.GetFileName(dll);
                foreach(var t in types){if(t==null||!t.IsClass||t.IsAbstract||t.IsInterface||t.Name.StartsWith("<")||t.Name.Contains("__")||t.Name=="ExtendedTestRunner")continue;
                bool at=false,mt=false,ih=false;
                try{at=t.GetCustomAttributes(true).Any(a2=>{var n=a2.GetType().Name.ToLower();return n.Contains("test")||n.Contains("fixture")||n.Contains("check");});}catch{}
                try{mt=t.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly).Any(m=>m.GetCustomAttributes(true).Any(a2=>{var n=a2.GetType().Name.ToLower();return n.Contains("test")||n.Contains("fact");}));}catch{}
                try{var bt=t.BaseType;while(bt!=null&&bt!=typeof(object)){if(bt.Name.ToLower().Contains("test")||bt.Name.ToLower().Contains("check")){ih=true;break;}bt=bt.BaseType;}}catch{}
                if(at||mt||ih||t.Name.StartsWith("MFG")||t.Name.StartsWith("AM")||t.Name.Contains("Test")||t.Name.EndsWith("Check")){
                    if(ts.All(x=>x.Name!=t.Name)){
                        var ti=new TestInfo{Name=t.Name,Dll=dllShort};
                        try{var da=t.GetCustomAttributes(true).FirstOrDefault(a2=>a2.GetType().Name.Contains("Description"));if(da!=null){var p2=da.GetType().GetProperties().FirstOrDefault();if(p2!=null)ti.Desc=p2.GetValue(da)?.ToString();}}catch{}
                        try{var ma=t.GetCustomAttributes(true).FirstOrDefault(a2=>a2.GetType().FullName?.StartsWith("Acumatica.")==true);if(ma!=null){var fn=ma.GetType().Name;if(fn.EndsWith("Attribute"))fn=fn.Substring(0,fn.Length-9);ti.Module=fn;mods.Add(fn);}}catch{}
                        ts.Add(ti);
                    }}}ld.Add(dll);}catch{}}
                AppDomain.CurrentDomain.AssemblyResolve-=rv;ts.Sort((a,b)=>string.Compare(a.Name,b.Name));
                return new{Tests=ts,Modules=mods.OrderBy(x=>x).ToList(),Loaded=ld};});

            _tests=r.Tests;_modules=r.Modules;_loadedDlls=r.Loaded;badgeDll.Visibility=Visibility.Collapsed;
            // Populate module filter
            cmbModule.Items.Clear();cmbModule.Items.Add(new ComboBoxItem{Content="All Modules",IsSelected=true});
            foreach(var m in _modules)cmbModule.Items.Add(new ComboBoxItem{Content=m});
            cmbModule.SelectedIndex=0;
            RefreshTests();RefreshDlls();UpdateBadge();
            txtDllStat.Text=$"{_tests.Count} tests from {r.Loaded.Count} DLL(s)";txtDllStat.Foreground=cOk;
            txtListTitle.Text=$"Tests ({_tests.Count})";pnlEmpty.Visibility=Visibility.Collapsed;
            }catch(Exception ex){txtDllStat.Text=$"Error: {ex.Message}";txtDllStat.Foreground=cErr;}finally{btnLoad.IsEnabled=true;btnLoad.Content="Load Selected";}
        }

        void BtnRefresh_Click(object s,RoutedEventArgs e){if(_chkDlls.Count==0&&_loadedDlls.Count==0)return;if(_chkDlls.Count==0)foreach(var d in _loadedDlls)_chkDlls.Add(d);var prev=new HashSet<string>(_sel);BtnLoad_Click(s,e);Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{foreach(var t in prev){var ti=_tests.FirstOrDefault(x=>x.Name==t);if(ti!=null)_sel.Add(t);}RefreshTests();UpdateBadge();}));}

        // ═══ TEST LIST WITH INLINE STATUS ═══
        List<TestInfo> GetFiltered()
        {
            string mod = (cmbModule?.SelectedItem as ComboBoxItem)?.Content?.ToString();
            string txt = txtFilter?.Text;
            var list = _tests.AsEnumerable();
            if (!string.IsNullOrEmpty(mod) && mod != "All Modules") list = list.Where(t => t.Module == mod);
            if (!string.IsNullOrWhiteSpace(txt)) list = list.Where(t => t.Name.IndexOf(txt, StringComparison.OrdinalIgnoreCase) >= 0);
            return list.ToList();
        }

        void RefreshTests()
        {
            if (pnlTests == null || txtStat == null) return;
            pnlTests.Children.Clear();
            var list = GetFiltered();
            if (_tests.Count > 0 && pnlEmpty != null) pnlEmpty.Visibility = Visibility.Collapsed;
            foreach (var t in list) pnlTests.Children.Add(MkRow(t));
            txtStat.Text = $"{list.Count} of {_tests.Count} tests";
        }

        Border MkRow(TestInfo t)
        {
            bool s=_sel.Contains(t.Name);
            var bd=new Border{Background=s?cSel:Brushes.White,BorderBrush=cBdr,BorderThickness=new Thickness(0,0,0,0.5),Padding=new Thickness(4,5,4,5),Cursor=System.Windows.Input.Cursors.Hand};
            var g=new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(30)});  // checkbox
            g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)}); // name
            g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(100)}); // module
            g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(120)}); // result
            g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(70)});  // duration
            g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(50)});  // logs

            var cb=new CheckBox{IsChecked=s,IsHitTestVisible=false,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(cb,0);
            var nm=new TextBlock{Text=t.Name,Foreground=cFg,FontSize=13,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};Grid.SetColumn(nm,1);
            var mod=new TextBlock{Text=t.Module??"",Foreground=cMod,FontSize=11,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(mod,2);

            // Result
            var resFg=t.Status=="PASSED"?cOk:t.Status=="FAILED"?cErr:t.Status=="Running..."||t.Status=="Restoring..."?cAcc:cFg3;
            var res=new TextBlock{Text=t.Status,Foreground=resFg,FontSize=12,FontWeight=t.Status=="Not Run"?FontWeights.Normal:FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(res,3);

            var dur=new TextBlock{Text=t.Duration??"",Foreground=cFg2,FontSize=11,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(dur,4);

            // Log/pic links
            var links=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(links,5);
            if(t.LogFile!=null&&File.Exists(t.LogFile)){var l=new TextBlock{Text="📄",FontSize=13,Foreground=cAcc,Cursor=System.Windows.Input.Cursors.Hand,Margin=new Thickness(0,0,4,0),ToolTip="View log"};
                string lp=t.LogFile;string tn=t.Name;
                l.MouseLeftButtonUp+=(a,b)=>{
                    // Switch to Results tab with cached log
                    if(_logCache.ContainsKey(tn))ShowCachedLog(tn);
                    else{try{Process.Start(new ProcessStartInfo{FileName=lp,UseShellExecute=true});}catch{}}
                };links.Children.Add(l);}
            if(t.LogFile!=null){var p=new TextBlock{Text="📸",FontSize=13,Foreground=cAcc,Cursor=System.Windows.Input.Cursors.Hand,ToolTip="Open screenshots"};
                string pd=txtPicDir.Text;p.MouseLeftButtonUp+=(a,b)=>{try{Process.Start(new ProcessStartInfo{FileName="explorer.exe",Arguments=pd});}catch{}};links.Children.Add(p);}

            g.Children.Add(cb);g.Children.Add(nm);g.Children.Add(mod);g.Children.Add(res);g.Children.Add(dur);g.Children.Add(links);
            bd.Child=g;
            if(!string.IsNullOrEmpty(t.Desc))bd.ToolTip=new ToolTip{Content=t.Desc,MaxWidth=500,FontSize=12};
            string name=t.Name;
            bd.MouseLeftButtonUp+=(a,e)=>{if(_sel.Contains(name))_sel.Remove(name);else _sel.Add(name);RefreshTests();UpdateBadge();};
            bd.MouseEnter+=(a,e)=>{if(!_sel.Contains(name))bd.Background=cHover;};
            bd.MouseLeave+=(a,e)=>{bd.Background=_sel.Contains(name)?cSel:Brushes.White;};
            return bd;
        }

        void UpdateBadge(){txtBadge.Text=$"{_sel.Count} test{(_sel.Count!=1?"s":"")} selected";}
        void Filter_Changed(object s,TextChangedEventArgs e)=>RefreshTests();
        void ModuleFilter_Changed(object s,SelectionChangedEventArgs e)=>RefreshTests();
        void BtnSelAll_Click(object s,RoutedEventArgs e){foreach(var t in GetFiltered())_sel.Add(t.Name);RefreshTests();UpdateBadge();}
        void BtnSelNone_Click(object s,RoutedEventArgs e){_sel.Clear();RefreshTests();UpdateBadge();}

        // ═══ EXECUTION ═══
        List<TestInfo> GetSelOrdered(){return _tests.Where(t=>_sel.Contains(t.Name)).ToList();}
        string MkXml(string t){string ll=(cmbLog.SelectedItem as ComboBoxItem)?.Content?.ToString()??"DEBUG";string ss=(cmbSS.SelectedItem as ComboBoxItem)?.Content?.ToString()??"true";return $"<?xml version=\"1.0\" encoding=\"utf-8\"?><config><general><browserbin>{txtBrowser.Text}</browserbin><browserheadless>{(chkHead.IsChecked==true?"true":"false")}</browserheadless><site_dst><rmhost>{txtRm.Text}</rmhost><url>{txtUrl.Text}</url><login>{txtLogin.Text}</login><pswd>{txtPwd.Text}</pswd><lang>{txtLang.Text}</lang><cmpid>{txtCmp.Text}</cmpid></site_dst><logging><logStorage type=\"txtfile\" level=\"{ll}\" outputFolder=\"{txtLogDir.Text}\" screenshotActive=\"{ss}\" screenshotOutputFolder=\"{txtPicDir.Text}\" /></logging></general><testing><Check Name= \"{t}\"/></testing></config>";}

        async Task<int> RunCmd(string exe,string args,CancellationToken ct){var psi=new ProcessStartInfo{FileName=exe,Arguments=args,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};_proc=Process.Start(psi);ct.Register(()=>{try{KillTree(_proc);}catch{}});await Task.Run(()=>_proc.WaitForExit(),ct);var c=_proc.ExitCode;_proc=null;return c;}
        void KillTree(Process p){if(p==null||p.HasExited)return;try{Process.Start(new ProcessStartInfo("taskkill",$"/T /F /PID {p.Id}"){UseShellExecute=false,CreateNoWindow=true})?.WaitForExit(5000);}catch{try{p.Kill();}catch{}}}
        async Task RestoreDb(string bak,CancellationToken ct){await RunCmd("sqlcmd",$"-E -S {txtSrv.Text} -Q \"ALTER DATABASE [{txtDb.Text}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE\"",ct);await RunCmd("sqlcmd",$"-E -S {txtSrv.Text} -Q \"RESTORE DATABASE [{txtDb.Text}] FROM DISK = N'{bak}' WITH FILE = 1, NOUNLOAD, REPLACE, STATS = 5\"",ct);await RunCmd("sqlcmd",$"-E -S {txtSrv.Text} -Q \"ALTER DATABASE [{txtDb.Text}] SET MULTI_USER\"",ct);}
        async Task BackupDb(string t,CancellationToken ct){string n=$"{t}_{txtVer.Text.Replace(".","_")}_{DateTime.Now:yyyyMMdd_HHmmss}";foreach(var c in Path.GetInvalidFileNameChars())n=n.Replace(c,'_');string d=txtBakOut.Text.Trim();if(!Directory.Exists(d))Directory.CreateDirectory(d);await RunCmd("sqlcmd",$"-E -S {txtSrv.Text} -Q \"BACKUP DATABASE [{txtDb.Text}] TO DISK = N'{Path.Combine(d,n+".bak")}' WITH FORMAT, INIT, NAME = N'{n}'\"",ct);}
        void ArchiveLogs(string t){string a=txtArcDir.Text.Trim();if(string.IsNullOrEmpty(a))return;string f=Path.Combine(a,$"{t}_{txtVer.Text.Replace(".","_")}_{DateTime.Now:yyyyMMdd_HHmmss}");try{Directory.CreateDirectory(Path.Combine(f,"logs"));Directory.CreateDirectory(Path.Combine(f,"pics"));if(Directory.Exists(txtLogDir.Text))foreach(var x in Directory.GetFiles(txtLogDir.Text))File.Copy(x,Path.Combine(f,"logs",Path.GetFileName(x)),true);if(Directory.Exists(txtPicDir.Text))foreach(var x in Directory.GetFiles(txtPicDir.Text))File.Copy(x,Path.Combine(f,"pics",Path.GetFileName(x)),true);}catch{}}
        string FindLog(string t){try{return Directory.GetFiles(txtLogDir.Text,$"{t}_*").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();}catch{return null;}}
        bool CheckFailed(string t){var l=FindLog(t);if(l==null)try{l=Directory.GetFiles(txtLogDir.Text,"Log_*").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();}catch{}if(l!=null)try{return File.ReadAllText(l).Contains("Error :");}catch{}return false;}

        void UpdateTestStatus(string name,string status,string duration=null,string logFile=null)
        {
            var t=_tests.FirstOrDefault(x=>x.Name==name);
            if(t!=null){t.Status=status;t.Duration=duration;t.LogFile=logFile;}
            RefreshTests();
        }

        async void BtnRun_Click(object s,RoutedEventArgs e)
        {
            if(_running)return;var tests=GetSelOrdered();if(tests.Count==0){MessageBox.Show("No tests selected.");return;}
            string list=string.Join("\n",tests.Take(5).Select(t=>"  • "+t.Name));if(tests.Count>5)list+=$"\n  +{tests.Count-5} more";
            if(MessageBox.Show($"Run {tests.Count} test(s)?\n\n{list}","Confirm",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            SaveS();_running=true;_cts=new CancellationTokenSource();_logCache.Clear();
            btnRun.Visibility=Visibility.Collapsed;btnStop.Visibility=Visibility.Visible;
            var bm=(BackupMode)cmbBak.SelectedIndex;string cfg=Path.Combine(Path.GetTempPath(),"QATestRunner_config.xml");

            if(chkDelLog.IsChecked==true){try{if(Directory.Exists(txtLogDir.Text))foreach(var f in Directory.GetFiles(txtLogDir.Text))File.Delete(f);}catch{}try{if(Directory.Exists(txtPicDir.Text))foreach(var f in Directory.GetFiles(txtPicDir.Text))File.Delete(f);}catch{}}

            // Reset all selected tests to "Not Run"
            foreach(var t in tests){t.Status="Not Run";t.Duration=null;t.LogFile=null;t.Failed=false;}
            RefreshTests();

            int ok=0,fail=0;
            try{for(int i=0;i<tests.Count;i++){if(_cts.Token.IsCancellationRequested)break;
                var test=tests[i];var sw=Stopwatch.StartNew();
                txtBadge.Text=$"Running {i+1}/{tests.Count}: {test.Name}";

                // 1. Restore
                UpdateTestStatus(test.Name,"Restoring...");
                try{await RestoreDb(txtBak.Text,_cts.Token);}catch{UpdateTestStatus(test.Name,"Restore Failed");fail++;continue;}
                if(_cts.Token.IsCancellationRequested)break;

                // 2. Run
                UpdateTestStatus(test.Name,"Running...");
                File.WriteAllText(cfg,MkXml(test.Name),Encoding.UTF8);
                try{await RunCmd(txtExe.Text,$"/config \"{cfg}\"",_cts.Token);}catch(OperationCanceledException){break;}catch{}
                sw.Stop();

                // 3. Check
                bool f=CheckFailed(test.Name);if(f)fail++;else ok++;
                test.Failed=f;string logFile=FindLog(test.Name);
                string dur=sw.Elapsed.TotalMinutes>=1?$"{(int)sw.Elapsed.TotalMinutes}m {sw.Elapsed.Seconds}s":$"{sw.Elapsed.Seconds}s";

                // 4. Backup
                bool doBak=bm==BackupMode.Always||(bm==BackupMode.FailedOnly&&f);
                if(doBak){UpdateTestStatus(test.Name,f?"FAILED — saving DB...":"PASSED — saving DB...",dur,logFile);try{await BackupDb(test.Name,_cts.Token);}catch{}}

                // 5. Archive
                ArchiveLogs(test.Name);

                // 6. Cache log parse in background
                if(logFile!=null&&File.Exists(logFile)){string lf=logFile;string tn=test.Name;
                    await Task.Run(()=>{try{var entries=ParseLog(File.ReadAllLines(lf));_logCache[tn]=entries;}catch{}});}

                // 7. Final status
                string st=f?"FAILED":"PASSED";if(doBak)st+=" (DB saved)";
                UpdateTestStatus(test.Name,st,dur,logFile);
            }}catch(OperationCanceledException){}catch(Exception ex){MessageBox.Show($"Error: {ex.Message}");}

            txtBadge.Text=_cts.Token.IsCancellationRequested?$"Stopped — {ok} passed, {fail} failed":$"Done — {ok} passed, {fail} failed";
            btnRun.Visibility=Visibility.Visible;btnStop.Visibility=Visibility.Collapsed;_running=false;_cts=null;
            if(chkSound.IsChecked==true)try{System.Media.SystemSounds.Exclamation.Play();}catch{}
        }

        void BtnStop_Click(object s,RoutedEventArgs e){_cts?.Cancel();if(_proc!=null&&!_proc.HasExited)KillTree(_proc);try{Process.Start(new ProcessStartInfo("sqlcmd",$"-E -S {txtSrv.Text} -Q \"ALTER DATABASE [{txtDb.Text}] SET MULTI_USER\""){UseShellExecute=false,CreateNoWindow=true});}catch{}txtBadge.Text="Stopping...";}

        // ═══ SHOW CACHED LOG (click 📄 on a test row) ═══
        void ShowCachedLog(string testName)
        {
            tabs.SelectedIndex=2; // switch to Results tab
            treeLog.Items.Clear();imgSS.Source=null;txtNoSS.Visibility=Visibility.Visible;btnOpenSS.Visibility=Visibility.Collapsed;_ssPath=null;
            if(!_logCache.ContainsKey(testName))return;
            var entries=_logCache[testName];var tree=BuildTree(entries);
            var ti=_tests.FirstOrDefault(x=>x.Name==testName);
            bool fail=entries.Any(x=>x.IsError);
            txtResStatus.Text=fail?"FAILED":"PASSED";txtResStatus.Foreground=fail?cErr:cOk;
            string t0=entries.FirstOrDefault()?.Time??"",t1=entries.LastOrDefault()?.Time??"";
            txtResDur.Text=CalcDur(t0,t1);
            foreach(var en in tree)treeLog.Items.Add(MkTree(en));

            // Also select it in the runs dropdown if present
            for(int i=0;i<cmbRuns.Items.Count;i++){if(cmbRuns.Items[i].ToString().StartsWith(testName)){cmbRuns.SelectedIndex=i;break;}}
        }

        // ═══ RESULTS TAB ═══
        void BtnLoadRuns_Click(object s,RoutedEventArgs e)
        {
            _runFiles.Clear();cmbRuns.Items.Clear();var dirs=new List<string>();
            if(Directory.Exists(txtLogDir.Text))dirs.Add(txtLogDir.Text);
            if(Directory.Exists(txtArcDir.Text))foreach(var sub in Directory.GetDirectories(txtArcDir.Text).OrderByDescending(Directory.GetCreationTime)){string l=Path.Combine(sub,"logs");if(Directory.Exists(l))dirs.Add(l);}
            foreach(var dir in dirs){var files=Directory.GetFiles(dir,"*.*").Where(f=>f.EndsWith(".txt")||f.EndsWith("DEBUG")||f.EndsWith("INFO")).OrderByDescending(File.GetLastWriteTime);
            foreach(var f in files){string fn=Path.GetFileName(f);bool sp=!fn.StartsWith("Log_");string tn;if(sp){int li=fn.IndexOf("_Log_");if(li>0)tn=fn.Substring(0,li);else continue;}else tn="(General)";
            bool arc=dir.Contains("archive");string label=arc?$"📁 {tn}  [{File.GetLastWriteTime(f):MM/dd HH:mm}]":$"{tn}  [{File.GetLastWriteTime(f):MM/dd HH:mm}]";
            _runFiles.Add(f);cmbRuns.Items.Add(label);}}
            if(cmbRuns.Items.Count>0)cmbRuns.SelectedIndex=0;
        }

        void CmbRuns_Changed(object s,SelectionChangedEventArgs e){if(cmbRuns.SelectedIndex>=0&&cmbRuns.SelectedIndex<_runFiles.Count)ShowLog(_runFiles[cmbRuns.SelectedIndex]);}
        void BtnOpenFolder_Click(object s,RoutedEventArgs e){if(Directory.Exists(txtLogDir.Text))Process.Start(new ProcessStartInfo{FileName="explorer.exe",Arguments=txtLogDir.Text});}

        void ShowLog(string path)
        {
            treeLog.Items.Clear();imgSS.Source=null;txtNoSS.Visibility=Visibility.Visible;btnOpenSS.Visibility=Visibility.Collapsed;_ssPath=null;
            if(!File.Exists(path))return;
            var entries=ParseLog(File.ReadAllLines(path));var tree=BuildTree(entries);
            bool fail=entries.Any(x=>x.IsError);
            txtResStatus.Text=fail?"FAILED":"PASSED";txtResStatus.Foreground=fail?cErr:cOk;
            string t0=entries.FirstOrDefault()?.Time??"",t1=entries.LastOrDefault()?.Time??"";
            txtResDur.Text=CalcDur(t0,t1);
            foreach(var en in tree)treeLog.Items.Add(MkTree(en));
        }

        // ═══ LOG PARSING ═══
        List<LogEntry> ParseLog(string[] lines)
        {
            var r=new List<LogEntry>();bool ie=false;StringBuilder eb=null;LogEntry cu=null;
            foreach(var raw in lines){if(string.IsNullOrWhiteSpace(raw)){if(ie)eb?.AppendLine();continue;}
            int d=0,i=0;while(i<raw.Length&&raw[i]=='\t'){d++;i++;}string tr=raw.Substring(i);
            if(ie){if(tr.Length>8&&tr[2]==':'&&tr[5]==':'){if(cu!=null){cu.Message+="\n"+eb.ToString().TrimEnd();r.Add(cu);}ie=false;eb=null;}else{eb?.AppendLine(tr);continue;}}
            if(tr.Length<13||tr[2]!=':'||tr[5]!=':'){if(cu!=null&&cu.IsError)cu.Message+="\n"+tr;continue;}
            string tm=tr.Substring(0,12).Trim();string rest=tr.Substring(12).Trim();int ci=rest.IndexOf(" : ");if(ci<0)continue;
            string tp=rest.Substring(0,ci).Trim(),mg=rest.Substring(ci+3).Trim();
            var en=new LogEntry{Time=tm,Type=tp,Message=mg,Depth=d,IsError=tp.Equals("Error",StringComparison.OrdinalIgnoreCase)};
            if(tp.Equals("Screenshot",StringComparison.OrdinalIgnoreCase))en.ScreenshotPath=mg.Trim();
            if(en.IsError){ie=true;eb=new StringBuilder();cu=en;continue;}if(cu!=null&&!r.Contains(cu))r.Add(cu);cu=en;r.Add(en);}
            if(ie&&cu!=null&&!r.Contains(cu)){cu.Message+="\n"+(eb?.ToString().TrimEnd()??"");r.Add(cu);}return r;
        }

        List<LogEntry> BuildTree(List<LogEntry> flat){var roots=new List<LogEntry>();var stk=new Stack<LogEntry>();foreach(var en in flat){while(stk.Count>0&&stk.Peek().Depth>=en.Depth)stk.Pop();if(stk.Count>0)stk.Peek().Children.Add(en);else roots.Add(en);stk.Push(en);}return roots;}

        TreeViewItem MkTree(LogEntry en)
        {
            var p=new StackPanel{Orientation=Orientation.Horizontal};
            p.Children.Add(new TextBlock{Text=Badge(en.Type),FontSize=10,FontWeight=FontWeights.Bold,Foreground=TCol(en.Type),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,6,0),MinWidth=18});
            p.Children.Add(new TextBlock{Text=en.Time,FontSize=10,Foreground=cFg3,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,6,0)});
            string dsp=en.Type=="Screenshot"&&en.ScreenshotPath!=null?"📸 "+Path.GetFileName(en.ScreenshotPath):(en.Message.Length>100?en.Message.Substring(0,97)+"...":en.Message);
            p.Children.Add(new TextBlock{Text=dsp,FontSize=12,Foreground=en.IsError?cErr:TCol(en.Type),FontWeight=(en.Type=="Testcase"||en.Type=="Step"||en.Type=="Check")?FontWeights.SemiBold:FontWeights.Normal,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=500});
            var item=new TreeViewItem{Header=p,Tag=en,IsExpanded=en.Type=="Check"||en.Type=="Testcase"||en.Type=="Step"};
            if(en.Type=="Operation"||en.Type=="Screenshot"||en.Type=="Information")item.IsExpanded=false;
            foreach(var c in en.Children)item.Items.Add(MkTree(c));return item;
        }

        string Badge(string t){switch(t){case"Check":return"▶";case"Testcase":return"TC";case"Step":return"→";case"Operation":return"⚙";case"Screenshot":return"📸";case"Information":return"ℹ";case"Error":return"✕";default:return"·";}}
        SolidColorBrush TCol(string t){switch(t){case"Check":return cAcc;case"Testcase":return cTc;case"Step":return cStep;case"Operation":return cOp;case"Screenshot":return cSS;case"Information":return cInfo;case"Error":return cErr;default:return cFg3;}}
        string CalcDur(string a,string b){try{var s=TimeSpan.Parse(a.Substring(0,8));var e2=TimeSpan.Parse(b.Substring(0,8));var d=e2-s;if(d.TotalSeconds<0)d=d.Add(TimeSpan.FromDays(1));return d.TotalHours>=1?$"{(int)d.TotalHours}h {d.Minutes}m":d.TotalMinutes>=1?$"{(int)d.TotalMinutes}m {d.Seconds}s":$"{d.Seconds}s";}catch{return"—";}}

        // ═══ SCREENSHOT VIEWER ═══
        void Tree_Selected(object s,RoutedPropertyChangedEventArgs<object> e){if(!(e.NewValue is TreeViewItem tv)||!(tv.Tag is LogEntry en))return;string sp=en.ScreenshotPath??en.Children.FirstOrDefault(c=>c.Type=="Screenshot")?.ScreenshotPath;if(sp!=null)LoadSS(sp);}
        void LoadSS(string raw)
        {
            string local=raw;if(raw.StartsWith("\\\\")){var pts=raw.TrimStart('\\').Split('\\');if(pts.Length>=3&&pts[1].EndsWith("$")){local=pts[1].Replace("$",":")+"\\"+ string.Join("\\",pts.Skip(2));}}
            string alt=Path.Combine(txtPicDir.Text,Path.GetFileName(raw));string found=File.Exists(local)?local:File.Exists(alt)?alt:File.Exists(raw)?raw:null;
            if(found!=null){try{var bi=new BitmapImage();bi.BeginInit();bi.CacheOption=BitmapCacheOption.OnLoad;bi.UriSource=new Uri(found);bi.EndInit();imgSS.Source=bi;txtNoSS.Visibility=Visibility.Collapsed;btnOpenSS.Visibility=Visibility.Visible;txtSSPath.Text=found;_ssPath=found;}catch{txtNoSS.Text=$"Could not load:\n{found}";txtNoSS.Visibility=Visibility.Visible;imgSS.Source=null;}}
            else{txtNoSS.Text=$"Not found:\n{Path.GetFileName(raw)}";txtNoSS.Visibility=Visibility.Visible;imgSS.Source=null;btnOpenSS.Visibility=Visibility.Collapsed;}
        }
        void BtnOpenSS_Click(object s,RoutedEventArgs e){if(_ssPath!=null&&File.Exists(_ssPath))Process.Start(new ProcessStartInfo{FileName=_ssPath,UseShellExecute=true});}
    }
}
