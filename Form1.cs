using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DrDiff
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }
        readonly byte[] drDSig = Encoding.UTF8.GetBytes("DRDIFF");
        MD5 Hash = MD5.Create();

        string OpenD(string Title)
        {
            var d = new FolderBrowserDialog();
            if (d.ShowDialog() == DialogResult.OK)
            {
                return d.SelectedPath.ToString();
            }
            return "";
        }

        string SaveD(string Title)
        {
            var d = new SaveFileDialog { DefaultExt = ".txt", Title = Title };
            if (d.ShowDialog() == DialogResult.OK)
            {
                return d.FileName;
            }
            return "";
        }

        private void button1_Click(object sender, EventArgs e)
        {
            textBox1.Text = OpenD("Select a directory") ?? textBox1.Text;
        }

        private void button2_Click(object sender, EventArgs e)
        {
            textBox2.Text = OpenD("Select a directory") ?? textBox2.Text;
        }

        private void button3_Click(object sender, EventArgs e)
        {
            textBox3.Text = SaveD("Save file") ?? textBox3.Text;
        }

        void AddLog(string Log)
        {
            string _Log = $"{DateTime.Now.ToString()} {Log}";
            listBox1.Invoke(new MethodInvoker(()=>
            {
                listBox1.Items.Add(_Log);
                listBox1.SelectedIndex = listBox1.Items.Count-1;
            }));
            Console.WriteLine(_Log);
        }

        List<string> GetRecurseDir(string dir)
        {
            AddLog($"Building a Recursive Directory List now, for {dir}");
            List<string> AllFiles = new List<string>();
            ConcurrentBag<string> Directories = new ConcurrentBag<string>(Directory.EnumerateDirectories(dir))
            {
                dir
            };
            List<String> NeedEnumDirs = Directories.ToList();
            while (NeedEnumDirs.LongCount() != 0)
            {

                AddLog($"Need to Enumerate through {NeedEnumDirs.LongCount()} directories.");
                ConcurrentBag<String> BufAdd = new ConcurrentBag<string>();
                List<Task> Tasks = new List<Task>();
                foreach (string Item in NeedEnumDirs)
                {
                    Tasks.Add(Task.Run(() =>
                    {
                        try
                        {
                            List<string> Dirs = Directory.EnumerateDirectories(Item).ToList();
                            foreach (var _Item in Dirs.Where(x => !Directories.Contains(x)))
                            {
                                BufAdd.Add(_Item);
                            }
                        } catch (Exception) {}
                    }));
                }
                AddLog($"Waiting for {Tasks.LongCount()} to finish.");
                Task.WaitAll(Tasks.ToArray());
                AddLog("Finished!");
                NeedEnumDirs = BufAdd.ToList();
                foreach (string _Item in BufAdd)
                {
                    Directories.Add(_Item);
                }
            }

            foreach (var Item in Directories.Distinct())
            {
                try
                {
                    AllFiles.AddRange(Directory.GetFiles(Item));
                }
                catch (Exception)
                {
                    continue;
                }
            }

            return AllFiles;
        }

        string HiveName(string D1, string D2)
        {
            return BitConverter.ToString(Hash.ComputeHash(Encoding.UTF8.GetBytes(D1 + D2))).Replace("-","");
        }

        Difference FileDiffers(string path, string path2)
        {
            FileInfo a = new System.IO.FileInfo(path);
            FileInfo b = new System.IO.FileInfo(path2);
            if (!a.Exists && !b.Exists)
            {
                throw new Exception("Deprecated hive, or files don't exist");
            }
            return new Difference
            {
                differs = (a.LastWriteTime != b.LastWriteTime) || a.Length != b.Length,
                path1 = path,
                path2 = path2,
                path1exists = a.Exists,
                path2exists = b.Exists
            };
        }

        void BuildHive()
        {
            if (textBox1.Text.Length == 0 || textBox2.Text.Length == 0)
            {
                AddLog($"Select both directories.");
                return;
            }
            string fn = $"{HiveName(textBox1.Text, textBox2.Text)}.hive.gz";
            if (File.Exists(fn))
            {
                AddLog($"Hive of these directories already exists.");
                return;
            }
            List<string> Hive = GetRecurseDir(textBox1.Text);
            Hive.AddRange(GetRecurseDir(textBox2.Text));
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Hive);
            FileStream compressedFileStream = File.OpenWrite(fn);
            byte[] sig = new byte[8];
            drDSig.CopyTo(sig, 0);
            if (bytes.Length > int.MaxValue)
            {
                // intellisense wrote the comment below
                drDSig[drDSig.Length - 1] = 0xFF; // just a little marker to know that this hive is uncompressed, because it's bigger than 2GB, and GZIPStream doesn't support int64 sizes, so if your hive is bigger than 2GB, you're fucked.
                AddLog($"Hive is bigger than 2GB, cannot be compressed. It will be stored AS-IS. Size: {Math.Round(Convert.ToDouble(bytes.Length) / 1024d / 1024d, 0)} MB");
            }
            else
            {
                using (var compressionStream = new GZipStream(compressedFileStream, CompressionMode.Compress, true))
                {
                    compressionStream.Write(bytes, 0, bytes.Length);
                }
            }


            AddLog($"Done building a hive, total weight: {Math.Round(Convert.ToDouble(bytes.Length) / 1024d / 1024d, 0)} MB");

            compressedFileStream.Write(sig, 0, 8);
            compressedFileStream.Write(BitConverter.GetBytes(Convert.ToInt64(bytes.Length)), 0, 8);
            compressedFileStream.Close();
            MessageBox.Show("Done building & Compressing Hive.");
        }

        List<string> LoadHive(string dir1, string dir2)
        {
            if (dir1.Length == 0 || dir2.Length == 0)
            {
                AddLog($"Select both directories.");
                return null;
            }
            string fn = $"{HiveName(dir1, dir2)}.hive.gz";
            if (!File.Exists(fn))
            {
                AddLog($"Hive of these directories does not exist. Build it first.");
                return null;
            }
            FileStream compressedFileStream = File.Open(fn, FileMode.Open);
            byte[] a = new byte[8];
            byte[] sig = new byte[8];
            compressedFileStream.Position = compressedFileStream.Length - 8;
            compressedFileStream.Read(a, 0, 8);
            compressedFileStream.Position = compressedFileStream.Length - 16;
            compressedFileStream.Read(sig, 0, 8);
            compressedFileStream.Position = 0;
            if(sig.Take(6).SequenceEqual(drDSig))
            {
                AddLog("Hive signature is valid.");
            }
            else
            {
                AddLog("Hive signature is invalid, this file is not a valid hive.");
                return null;
            }
            long size = BitConverter.ToInt64(a.ToArray(), 0);

            MemoryStream ms = new MemoryStream();
            compressedFileStream.CopyTo(ms);
            compressedFileStream.Close();
            ms.Position = 0;
            byte [] buf = new byte[size];
            if(sig.Last() != 0xFF) // if a compressed stream
            {
                using (var deCompressionStream = new GZipStream(ms, CompressionMode.Decompress))
                {
                    AddLog($"Decompressing hive..");
                    int read = -1;
                    int read_ = 0;
                    while (read != 0)
                    {
                        int wR = size < 81920 ? Convert.ToInt32(size) : 81920; // will read
                        if(read_+wR >= size)
                        {
                            wR = Convert.ToInt32(size - read_);
                        }
                        read = deCompressionStream.Read(buf, read_, wR);
                        read_ += read;
                        AddLog($"Reading hive - {Math.Round(Convert.ToDouble(read_) / 1024d, 2)} KB in");
                    }
                }
            }
            else
            {
                ms.ToArray().CopyTo(buf, 0); // if uncompressed, just copy the bytes to the buffer, no need to decompress.
            }

            AddLog($"Done loading a hive, total weight: {Math.Round(Convert.ToDouble(size) / 1024d / 1024d, 0)} MB");
            return JsonSerializer.Deserialize<List<string>>(Encoding.UTF8.GetString(buf));
        }

        private void button6_Click(object sender, EventArgs e)
        {
            var th = new Thread(() =>
            {
                BuildHive();
            });
            th.Start();
        }

        private void button4_Click(object sender, EventArgs e)
        {
            foreach (var item in Directory.GetFiles(Directory.GetCurrentDirectory(), "*.hive.gz"))
            {
                try
                {
                    File.Delete(item);
                }
                catch (Exception)
                {
                    AddLog($"Failed to remove hive: {item}");
                }
            }
            AddLog("Removed all cached hives.");
        }

        void GetDifferences(string dir1, string dir2, string FileOut)
        {
            List<string> Hive = LoadHive(dir1, dir2);
            ConcurrentBag<Difference> Differences = new ConcurrentBag<Difference>();
            List<Task> Tasks = new List<Task>();
            List<string> FileNames = Hive.Select(x => Regex.Split(x, Regex.Escape(dir1)).Last()).ToList();
            FileNames.AddRange(Hive.Select(x => Regex.Split(x, Regex.Escape(dir2)).Last()).ToList());
            FileNames = FileNames.Distinct().ToList();
            AddLog($"Starting comparison of {FileNames.LongCount()} files.");
            foreach (var item in FileNames)
            {
                Tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        string path1 = Path.Combine(dir1, item.TrimStart(Path.DirectorySeparatorChar));
                        string path2 = Path.Combine(dir2, item.TrimStart(Path.DirectorySeparatorChar));
                        Differences.Add(FileDiffers(path1, path2));
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                        // ignored
                    }
                }));
            }
            AddLog($"Waiting for {Tasks.LongCount()} to finish.");
            Task.WaitAll(Tasks.ToArray());
            List<Difference> ActDifferences = Differences.Where(x => x.differs || (x.path1exists != x.path2exists)).ToList();

            AddLog($"Found {ActDifferences.LongCount()} differences, saving..");
            File.WriteAllText(FileOut, JsonSerializer.Serialize(ActDifferences), Encoding.UTF8);
            AddLog("Done!");
            MessageBox.Show("Done comparing directories! Differences saved to file.");
        }

        private void button5_Click(object sender, EventArgs e)
        {
            var th = new Thread(() =>
            {
                GetDifferences(textBox1.Text, textBox2.Text, textBox3.Text);
            });
            th.Start();
        }
    }

    public class Difference
    {
        public bool differs { get; set; }
        public string path1{ get; set; }
        public string path2 { get; set; }
        public bool path1exists { get; set; }
        public bool path2exists { get; set; }
    }
}
