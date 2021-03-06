﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Newtonsoft.Json;
using System.Windows.Interop;
using System.Globalization;
using System.Drawing.Imaging;
using System.Net.NetworkInformation;

namespace Client
{
    /// <summary>
    /// Logica di interazione per MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private TcpClient Client { get; set; }                          //Fornisce connessioni client per i servizi di rete TCP.
        private NetworkStream Stream { get; set; }                      //Stream per leggere e scrivere dal server
        private bool Connesso { get; set; }                             //Variabile booleana che identifica la connessione attiva
        private Thread Ascolta { get; set; }                            //Thread in ascolto sul server
        private Thread Riassunto { get; set; }                          //Thread che gestisce il riassunto
        private ObservableCollection<Applicazione> Apps { get; set; }   //Collezione che contiene le applicazioni del server
        private byte[] Combinazione { get; set; }                       //Combinazione da inviare
        private uint ProcessoFocus { get; set; }                        //Codice del processo in focus
        private Stopwatch TempoC { get; set; }                          //Cronometro di connessione al server
        readonly object key = new object();
        private object _lock = new object();

        public MainWindow()
        {
            InitializeComponent();
            Show();
            Thread.CurrentThread.Name = "Main";
            SetUp();
        }

        private void SetUp()
        {
            Client = null;
            Stream = null;
         //   Stream.ReadTimeout = 100;
            Connesso = false;
            Combinazione = new Byte[4];
            ProcessoFocus = 0;
            Apps = new ObservableCollection<Applicazione>();
            BindingOperations.EnableCollectionSynchronization(Apps, _lock);
            listView.ItemsSource = Apps;
            TempoC = new Stopwatch();
        }

        /// <summary>
        /// Questo metodo gestisce la connessione al server all'indirizzo
        /// IP e alla porta forniti nelle textBox corrispondenti.
        /// Prima di iniziare controlla che questi campi contengano dati.
        /// In tal caso, inizia il tentativo di connessione. 
        /// Alla fine della connessione attiva i thread paralleli che si
        /// occupano delle altre funzioni richieste.
        /// Se il client è già connesso, provvede alla disconnessione.
        /// </summary>
        private void connetti_Click(object sender, RoutedEventArgs e)
        {
            if (!Connesso)
            {
                testo.Text = "";
                if (string.IsNullOrWhiteSpace(indirizzo.Text))
                {
                    testo.AppendText("Il campo Indirizzo non può essere vuoto!\n");
                    return;
                }
                if (string.IsNullOrWhiteSpace(porta.Text))
                {
                    testo.AppendText("Il campo Porta non può essere vuoto.\n");
                    return;
                }
                try
                {
                    IPAddress ind = IPAddress.Parse(indirizzo.Text);
                    int port = int.Parse(porta.Text);
                    Client = new TcpClient();
                    var result = Client.BeginConnect(ind, port, null, null);
                    var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                    if (success)
                    {
                        TempoC.Restart();
                        connetti.Content = "Disconnetti";
                        testo.AppendText("Connesso a " + indirizzo.Text + ":" + porta.Text + ".\n");
                        Connesso = true;
                        Stream = Client.GetStream();
                        Ascolta = new Thread(ascoltaServer);
                        Ascolta.IsBackground = true;
                        Ascolta.Start();
                        Riassunto = new Thread(monitora);
                        Riassunto.IsBackground = true;
                        Riassunto.Start();
                        indirizzo.IsReadOnly = true;
                        porta.IsReadOnly = true;
                        //controllaConnessione(ind);
                    }
                    else
                    {
                        testo.AppendText("Connessione fallita.\n");
                    }
                }
                catch (FormatException ex)
                {
                    testo.AppendText("Errore nella specifica di indirizzo IP o porta.\n");
                    testo.AppendText(ex.StackTrace);
                }
                catch (Exception ex)
                {
                    testo.AppendText("Errore: " + ex.StackTrace);
                    if (Client != null)
                    {
                        Client.Close();
                        Client = null;
                    }
                    if (Ascolta != null)
                        Ascolta.Abort();
                    if (Riassunto != null)
                        Riassunto.Abort();
                    Connesso = false;
                    connetti.Content = "Connetti";
                }
            }
            else
            {
                TempoC.Stop();
                Connesso = false;
                Ascolta.Join();
                Riassunto.Join();
                Client.Close();
                ProcessoFocus = 0;
                Client = null;
                Apps.Clear();
                connetti.Content = "Connetti";
                indirizzo.IsReadOnly = false;
                porta.IsReadOnly = false;
            }
        }

        /// <summary>
        /// Il metodo permette al thread principale di controllare
        /// se il socket ha ancora una connessione attiva al server.
        /// In caso di disconnessione brutale da parte del server,
        /// avvia il processo di disconnessione.
        /// </summary>
        private void controllaConnessione(IPAddress ip)
        {
            Ping ping = new Ping();
            PingReply reply;
            do
            {
                reply = ping.Send(ip, 1000);
            } while (reply.Status == IPStatus.Success);
            testo.AppendText("Il server si è disconnesso.\n");
            TempoC.Stop();
            Connesso = false;
            Ascolta.Join();
            Riassunto.Join();
            Client.Close();
            Client = null;
            connetti.Content = "Connetti";
            indirizzo.IsReadOnly = false;
            porta.IsReadOnly = false;
        }

        /// <summary>
        /// Il metodo ascolta il server fin tanto che il client vi è connesso
        /// All'apertura istanzia il buffer di lettura, poi se lo stream
        /// è disponibile, ne legge un byte che conterrà un codice identificativo
        /// dell'operazione:
        /// n -> nuova applicazione
        /// a -> l'applicazione cambia nome e/o icona
        /// f -> focus cambiato
        /// r -> applicazione chiusa
        /// In tutti e tre i casi, legge la lunghezza del messaggio inviato
        /// e poi procede agli aggiornamenti individuati dalla notifica.
        /// </summary>
        private void ascoltaServer()
        {
            Thread.CurrentThread.Name = "Ascolto";
           
            Action action;
            Applicazione a;
            bool pronto = false;
            byte[] readBuffer = new byte[Client.ReceiveBufferSize];
            int nBytes, len, n;
            uint processo;

            try
            {
                while (Connesso)
                {
                    if (Stream.CanRead)
                    {
                        if (Stream.DataAvailable) {
                            Stream.ReadTimeout = 3000;
                            n = Stream.Read(readBuffer, 0, 4);
                            if (n == 0) //No data available
                                break;
                            len = BitConverter.ToInt32(readBuffer, 0);
                            Array.Clear(readBuffer, 0, 4);
                            Stream.Read(readBuffer, 0, 1);
                            char c = BitConverter.ToChar(readBuffer, 0);
                            Array.Clear(readBuffer, 0, 1);
                            switch (c)
                            {
                                //Inserimento nuova applicazione
                                //Messaggio: len-'a'-JSON(App)
                                case 'n':
                                    nBytes = 0;
                                    string lettura = "";
                                    string res = "";
                                    do
                                    {
                                        n = Stream.Read(readBuffer, nBytes, len);
                                        len -= n;
                                        nBytes += n;
                                        res = Encoding.Default.GetString(readBuffer);
                                        lettura += res;
                                    } while (len > 0);
                                    Array.Clear(readBuffer, 0, nBytes);
                                    a = JsonConvert.DeserializeObject<Applicazione>(lettura);
                                    if (a.IconLength != 0)
                                    {
                                        //if (Stream.DataAvailable)
                                        //{
                                            byte[] iconByte = new byte[a.IconLength];
                                            nBytes = 0;
                                            do
                                            {
                                                n = Stream.Read(iconByte, nBytes, a.IconLength);
                                                a.IconLength -= n;
                                                nBytes += n;
                                            } while (a.IconLength > 0);
                                            ottieniIcona(a, iconByte);
                                            Array.Clear(iconByte, 0, a.IconLength);
                                        //}
                                    }
                                    if (a.Focus)
                                    {
                                        if (ProcessoFocus != 0)
                                        {
                                            lock (_lock)
                                            {
                                                Apps.Where(i => i.Process == ProcessoFocus).Single().Focus = false;
                                                Apps.Where(i => i.Process == ProcessoFocus).Single().TempoF.Stop();
                                            }
                                        }
                                        a.TempoF.Start();
                                        ProcessoFocus = a.Process;
                                    }
                                    lock (_lock)
                                    {
                                        if (Apps.Where(i=>i.Process==a.Process).Count() == 0)
                                            Apps.Add(a);
                                    }
                                    action = () => testo.AppendText("Nuova applicazione: " + a.Name + ".\n");
                                    Dispatcher.Invoke(action);
                                    if (!pronto)
                                    {
                                        pronto = true;
                                        lock (key)
                                            Monitor.Pulse(key);
                                    }
                                    break;
                                //Aggiornamento di un'applicazione
                                //Messaggio: len-'a'-JSON(app)
                                case 'a':
                                    nBytes = 0;
                                    lettura = "";
                                    res = "";
                                    do
                                    {
                                        n = Stream.Read(readBuffer, nBytes, len);
                                        len -= n;
                                        nBytes += n;
                                        res = Encoding.Default.GetString(readBuffer);
                                        lettura += res;
                                    } while (len > 0);
                                    Array.Clear(readBuffer, 0, nBytes);
                                    Applicazione app = JsonConvert.DeserializeObject<Applicazione>(lettura);
                                    if (app.IconLength != 0)
                                    {
                                        byte[] iconByte = new byte[app.IconLength];
                                        nBytes = 0;
                                        do
                                        {
                                            n = Stream.Read(iconByte, nBytes, app.IconLength);
                                            app.IconLength -= n;
                                            nBytes += n;
                                        } while (app.IconLength > 0);
                                        ottieniIcona(app, iconByte);
                                        Array.Clear(iconByte, 0, app.IconLength);
                                    }
                                    lock (_lock)
                                    {
                                        a = Apps.Where(i => i.Process == app.Process).Single();
                                        a.Name = app.Name;
                                        a.Icona = app.Icona;
                                    }
                                    if (app.Focus) // se ora ha il focus ma prima no
                                    {
                                        if (!a.Focus)
                                        {
                                            lock (_lock)
                                            {
                                                Applicazione af = Apps.Where(i => i.Process == ProcessoFocus).Single();
                                                af.Focus = false;
                                                af.TempoF.Stop();
                                            }
                                            ProcessoFocus = a.Process;
                                            a.Focus = true;
                                            a.TempoF.Start();
                                            action = () => testo.AppendText("Nuovo focus: " + a.Name + ".\n");
                                            Dispatcher.Invoke(action);
                                        }
                                    }
                                    action = () => testo.AppendText("Applicazione aggiornata: " + a.Name + ".\n");
                                    Dispatcher.Invoke(action);
                                    break;
                                //Rimozione di un'applicazione
                                //Messaggio: len-'r'-Codice processo App
                                case 'r':
                                    Stream.Read(readBuffer, 0, 4);
                                    processo = BitConverter.ToUInt32(readBuffer, 0);
                            
                                    lock (_lock)
                                    {
                                        a = Apps.Where(i => i.Process == processo).Single();
                                        if (a.Focus)
                                        {
                                            a.Focus = false;
                                            a.TempoF.Stop();
                                           ProcessoFocus = 0;
                                        }
                                        Apps.Remove(a);
                                    }
                                    action = () => testo.AppendText("Applicazione rimossa: " + a.Name + ".\n");
                                    Dispatcher.Invoke(action);
                                    Array.Clear(readBuffer, 0, 4);
                                    break;
                                //Focus cambiato
                                //Messaggio: len-'f'-Codice processo App
                                case 'f':
                                    if (ProcessoFocus != 0)
                                    {
                                        lock(_lock)
                                        {
                                            a = Apps.Where(i => i.Process == ProcessoFocus).Single();
                                           
                                        }
                                        a.Focus = false;
                                        a.TempoF.Stop();

                                    }
                                    Stream.Read(readBuffer, 0, 4); 
                                    processo = BitConverter.ToUInt32(readBuffer, 0);
                                    
                                    lock (_lock)
                                    {
                                        action = () => testo.AppendText("ProcessoF: " + ProcessoFocus + " \nProcesso: " + processo + ".\n");
                                        Dispatcher.Invoke(action);
                                        a = Apps.Where(i => i.Process == processo).Single();
                                        
                                    }
                                    a.Focus = true;
                                    a.TempoF.Start();
                                    ProcessoFocus = processo;
                                    action = () => testo.AppendText("Nuovo focus: " + a.Name + ".\n");
                                    Dispatcher.Invoke(action);
                                    Array.Clear(readBuffer, 0, 4);
                                    break;
                                default:
                                    break;
                            }
                        }
                    }
                }
                return;
            }
            catch (IOException ex)
            {
                action = () => testo.AppendText(ex + ex.StackTrace + "\n");
                Dispatcher.Invoke(action);
            }
            catch(Exception ex)
            {
                action = () => testo.AppendText(ex + ex.StackTrace + "\n");
                Dispatcher.Invoke(action);
            }
        }

        private void leggiStream(NetworkStream stream, byte[] readBuffer, int len)
        {
            int bytesLetti = 0;
            int bytesDaLeggere = len;
            while (bytesDaLeggere > 0)
            {
                int n = stream.Read(readBuffer, bytesLetti, bytesDaLeggere);
                bytesLetti += n;
                bytesDaLeggere -= n;
            }

        }

        private void ottieniIcona(Applicazione a, byte[] iconByte)
        {
            using (MemoryStream ms = new MemoryStream(iconByte))
            {
                a.Icona = new Icon(ms);
            }
        }

        /// <summary>
        /// Il metodo gestisce il riassunto grafico, aggiornando con
        /// un thread dedicato le informazioni nel datagrid in base
        /// alle informazioni ricevute dal server, che sono trasmesse
        /// alla struttura applicazioni. Ogni volta che in quella struttura
        /// si verificherà una modifica, questa sarà riportata sul riassunto.
        /// </summary>
        private void monitora()
        {
            Thread.CurrentThread.Name = "Riassunto";
            //Finché non ci sono applicazioni nel dizionario è inutile monitorare.
            lock (key)
                Monitor.Wait(key);
            //Il monitoraggio dura fino alla fine della connessione
            while (Connesso)
            {
                if (ProcessoFocus != 0)
                {
                    lock (_lock)
                    {
                        foreach (var a in Apps.ToList())
                        {
                            a.Percentuale = (a.TempoF.Elapsed.TotalMilliseconds / TempoC.Elapsed.TotalMilliseconds) * 100;
                        }
                    }
                }
            }
            return;
        }
        private void invia_Click(object sender, RoutedEventArgs e)
        {
            if (!Connesso)
            {
                testo.AppendText("Devi essere connesso per poter inviare tasti all'applicazione in focus!\n");
                return;
            }
            Thread inviaTasti = new Thread(invioFocus);
            inviaTasti.IsBackground = true;
            inviaTasti.Start();
        }

        /// <summary>
        /// Il metodo si occupa di inviare la combinazione
        /// all'applicazione in focus.
        /// </summary>
        private void invioFocus()
        {
            Thread.CurrentThread.Name = "Invio";
            ConsoleKeyInfo cki;
            Action action;
            ConsoleManager.AllocaConsole();
            ConsoleManager.Show();
            Console.Write("Digita i tasti che vuoi inviare all'applicazione in focus...\n");
            Array.Clear(Combinazione, 0, Combinazione.Length);
            cki = Console.ReadKey();
            action = () => testo.AppendText("--- Hai premuto ");
            Dispatcher.Invoke(action);
            if ((cki.Modifiers & ConsoleModifiers.Alt) != 0)
            {
                action = () => testo.AppendText("ALT+");
                Dispatcher.Invoke(action);
                Combinazione[1] = 0x12;
            }
            if ((cki.Modifiers & ConsoleModifiers.Shift) != 0)
            {
                action = () => testo.AppendText("SHIFT+");
                Dispatcher.Invoke(action);
                Combinazione[2] = 0x10;
            }
            if ((cki.Modifiers & ConsoleModifiers.Control) != 0)
            {
                action = () => testo.AppendText("CTRL+");
                Dispatcher.Invoke(action);
                Combinazione[3] = 0x11;
            }
            action = () => testo.AppendText(cki.Key.ToString() + "\n");
            Dispatcher.Invoke(action);
            Combinazione[0] = Convert.ToByte(cki.Key);
            ConsoleManager.Hide();
            action = () => testo.AppendText("Sto inviando la combinazione!\n");
            Dispatcher.Invoke(action);
            if (Combinazione != null && ProcessoFocus != 0)
            {
                byte[] cod_proc = BitConverter.GetBytes(ProcessoFocus);
                int lung = Combinazione.Length;
                byte[] cod_lung = BitConverter.GetBytes(lung);
                byte[] buffer = new byte[cod_lung.Length + cod_proc.Length + Combinazione.Length];
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(cod_lung);
                    uint proc = BitConverter.ToUInt32(cod_proc, 0);
                }
                Buffer.BlockCopy(cod_lung, 0, buffer, 0, cod_lung.Length);
                Buffer.BlockCopy(cod_proc, 0, buffer, cod_lung.Length, cod_proc.Length);
                Buffer.BlockCopy(Combinazione, 0, buffer, cod_lung.Length + cod_proc.Length, Combinazione.Length);
                if (Stream.CanWrite)
                {
                    Stream.Write(buffer, 0, buffer.Length);
                }
            }
            return;
        }
    }
}

namespace System.Windows.Media
{
    /// <summary>
    /// One-way converter from System.Drawing.Image to System.Windows.Media.ImageSource
    /// </summary>
    [ValueConversion(typeof(System.Drawing.Icon), typeof(System.Windows.Media.ImageSource))]
    public class ImageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            // empty images are empty...
            if (value == null) { return null; }

            var image = (System.Drawing.Icon)value;
            // Winforms Image we want to get the WPF Image from...
            Bitmap bitmap = image.ToBitmap();
            IntPtr hBitmap = bitmap.GetHbitmap();

            ImageSource wpfBitmap = Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                    hBitmap, IntPtr.Zero, Int32Rect.Empty,
                                    BitmapSizeOptions.FromEmptyOptions());

            return wpfBitmap;
        }

        public object ConvertBack(object value, Type targetType,
            object parameter, CultureInfo culture)
        {
            return null;
        }
    }
}