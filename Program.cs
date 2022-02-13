using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using EternalUtils.API;

namespace XYplorerPatch
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            uint _IMAGE_BASE = 0x400000;
            uint _CODE_BASE_START = 0x401000;
            int _CODE_CAVE_SIZE = 21;
            string _CODE_CAVE_SIG = "";
            for (int i = 0; i < _CODE_CAVE_SIZE; i++) _CODE_CAVE_SIG += "00 ";
            string _INLINE_SIG = "89 ?? ?? 8d 48 ff 83 f9 ?? 0f 87 ?? ?? ?? ?? ff";
            uint _INJECTION, _INJECTION_OFFSET, _CODE_CAVE, _CODE_CAVE_OFFSET;
            long scan;
            string path = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + 
                        @"\XYplorer\XYplorer.exe";

            Console.WriteLine("XYplorer Patch 1.5 by Doug, tested on XYplorer 17.70.0200.");
            Console.WriteLine();

            bool killedProcess = false;
            FileStream fs = null;
            try
            {
                if (!File.Exists(path))
                {
                    var dlg = new OpenFileDialog();
                    dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                    dlg.Multiselect = false;
                    dlg.Title = "Locate XYplorer.exe";
                    dlg.Filter = "XYplorer Executable|XYplorer.exe|All files (*.*)|*.*";
                    if (dlg.ShowDialog() != DialogResult.OK)
                    {
                        Console.WriteLine("Canceled!");
                        return;
                    }

                    path = dlg.FileName;
                }

                foreach (var process in Process.GetProcessesByName("xyplorer"))
                {
                    if (process.MainModule.FileName == path)
                    {
                        if (!killedProcess)
                        {
                            killedProcess = true;
                            Console.WriteLine("Killing process.");
                        }
                        process.Kill();
                        process.WaitForExit();
                    }
                }

                Console.WriteLine("Processing {0}.", path);

                fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
                var read = new BinaryReader(fs);
                var write = new BinaryWriter(fs);

                // Find the inline injection location
                fs.Seek(_CODE_BASE_START - _IMAGE_BASE + 0x200000, SeekOrigin.Begin);
                scan = MemoryAPI.AOBScan(fs, _INLINE_SIG);
                if (scan == -1)
                {
                    fs.Seek(_CODE_BASE_START - _IMAGE_BASE, SeekOrigin.Begin);
                    scan = MemoryAPI.AOBScan(fs, _INLINE_SIG, null, 0x200000);
                    if (scan == -1)
                    {
                        Console.WriteLine("Possibly already patched!");
                        return;
                    }
                }
                _INJECTION_OFFSET = (uint) scan + 3;
                _INJECTION = _INJECTION_OFFSET + _IMAGE_BASE;
                Console.WriteLine("Found inline injection location at 0x{0}.", _INJECTION.ToString("X"));

                // Find a code cave
                fs.Seek(_CODE_BASE_START - _IMAGE_BASE, SeekOrigin.Begin);
                scan = MemoryAPI.AOBScan(fs, _CODE_CAVE_SIG);
                if (scan == -1)
                {
                    Console.WriteLine("Couldn't find a code cave big enough for our injection!");
                    return;
                }
                _CODE_CAVE_OFFSET = (uint)scan;
                _CODE_CAVE = _CODE_CAVE_OFFSET + _IMAGE_BASE;
                Console.WriteLine("Found code cave at 0x{0}.", _CODE_CAVE.ToString("X"));
                
                // Backup the original
                Console.WriteLine("Backing up to {0}.bak.", path);
                if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
                File.Copy(path, path + ".bak");

                Console.Write("Patching.. ");
                // Write the inline injection to jump to our code cave
                byte[] inlineInjection = MemoryAPI.BuildBuffer(
                    "E9 ?? ?? ?? ??" // JMP codecave
                    , new uint[] { 1, _CODE_CAVE - (_INJECTION + 5) });

                fs.Seek(_INJECTION_OFFSET, SeekOrigin.Begin);
                write.Write(inlineInjection);

                // Read the JMP TABLE COUNTER for our use in the code cave
                byte jmpTableCounter = read.ReadByte();

                // Build the code cave data
                byte[] codeCaveData = MemoryAPI.BuildBuffer(
                      "8D 48 FF "       // (1) LEA ECX,DWORD PTR DS:[EAX-1]
                    + "83 F9 0C "       // (2) CMP ECX,0C
                    + "74 07 "          // (3) JE (5)
                    + "83 F9 0D "       // (2) CMP ECX,0D
                    + "74 02 "          // (3) JE (5)
                    + "EB 05 "          // (3) JMP (5)
                    + "B9 05 00 00 00 " // (4) MOV ECX,5
                    + "83 F9 ?? "       // (5) CMP ECX,*JMP TABLE COUNTER*
                    + "E9 ?? ?? ?? ??"  // (6) JMP (JMP BACK)
                    );
                codeCaveData[22] = jmpTableCounter; // insert the JMP TABLE COUNTER
                MemoryAPI.PatchBuffer(ref codeCaveData, new uint[] { 24, _INJECTION + 6 - (_CODE_CAVE + (uint) codeCaveData.Length) }); // insert the JMP BACK address

                fs.Seek(_CODE_CAVE_OFFSET, SeekOrigin.Begin);
                write.Write(codeCaveData);

                fs.Close();
                Console.WriteLine("Done.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: {0}", ex);
            }
            finally
            {
                if (fs != null) fs.Close();

                if (killedProcess)
                {
                    Console.WriteLine("Restarting process.");
                    Process.Start(path);
                }

                Console.WriteLine("Press any key to close.");
                Console.ReadKey();
            }
        }
    }
}
