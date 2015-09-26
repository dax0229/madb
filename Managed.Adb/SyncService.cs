﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.IO;
using System.Net.Sockets;
using Managed.Adb.IO;

namespace Managed.Adb
{
    public class SyncService : ISyncService, IDisposable
    {
        /// <summary>
        /// Logging tag
        /// </summary>
        private const string TAG = "SyncService";

        private const string OKAY = "OKAY";
        private const string FAIL = "FAIL";
        private const string STAT = "STAT";
        private const string RECV = "RECV";
        private const string DATA = "DATA";
        private const string DONE = "DONE";
        private const string SEND = "SEND";
        private const string LIST = "LIST";
        private const string DENT = "DENT";

        [Flags]
        public enum FileMode
        {
            /// <summary>
            /// The unknown
            /// </summary>
            UNKNOWN = 0x0000, // unknown
            /// <summary>
            /// The socket
            /// </summary>
            Socket = 0xc000, // type: socket
            /// <summary>
            /// The symbolic link
            /// </summary>
            SymbolicLink = 0xa000, // type: symbolic link
            /// <summary>
            /// The regular
            /// </summary>
            Regular = 0x8000, // type: regular file
            /// <summary>
            /// The block
            /// </summary>
            Block = 0x6000, // type: block device
            /// <summary>
            /// The directory
            /// </summary>
            Directory = 0x4000, // type: directory
            /// <summary>
            /// The character
            /// </summary>
            Character = 0x2000, // type: character device
            /// <summary>
            /// The fifo
            /// </summary>
            FIFO = 0x1000  // type: fifo
        }

        /*private const int S_ISOCK = 0xc000; // type: symbolic link
        private const int S_IFLNK = 0xa000; // type: symbolic link
        private const int S_IFREG = 0x8000; // type: regular file
        private const int S_IFBLK = 0x6000; // type: block device
        private const int S_IFDIR = 0x4000; // type: directory
        private const int S_IFCHR = 0x2000; // type: character device
        private const int S_IFIFO = 0x1000; // type: fifo

        private const int S_ISUID = 0x0800; // set-uid bit
        private const int S_ISGID = 0x0400; // set-gid bit
        private const int S_ISVTX = 0x0200; // sticky bit
        private const int S_IRWXU = 0x01C0; // user permissions
        private const int S_IRUSR = 0x0100; // user: read;
        private const int S_IWUSR = 0x0080; // user: write;
        private const int S_IXUSR = 0x0040; // user: execute;
        private const int S_IRWXG = 0x0038; // group permissions
        private const int S_IRGRP = 0x0020; // group: read;
        private const int S_IWGRP = 0x0010; // group: write;
        private const int S_IXGRP = 0x0008; // group: execute;
        private const int S_IRWXO = 0x0007; // other permissions
        private const int S_IROTH = 0x0004; // other: read;
        private const int S_IWOTH = 0x0002; // other: write;
        private const int S_IXOTH = 0x0001; // other: execute;*/

        private const int SYNC_DATA_MAX = 64 * 1024;
        private const int REMOTE_PATH_MAX_LENGTH = 1024;

        #region static members
        static SyncService()
        {
            NullProgressMonitor = new NullSyncProgressMonitor();
        }

        /// <summary>
        /// Gets the null progress monitor.
        /// </summary>
        /// <value>
        /// The null progress monitor.
        /// </value>
        public static NullSyncProgressMonitor NullProgressMonitor { get; private set; }
        private static byte[] DataBuffer { get; set; }

        /// <summary>
        /// Checks the result array starts with the provided code
        /// </summary>
        /// <param name="result">The result array to check</param>
        /// <param name="code">The 4 byte code.</param>
        /// <returns>true if the code matches.</returns>
        private static bool CheckResult(byte[] result, byte[] code)
        {

            if (result.Length >= code.Length)
            {
                for (int i = 0; i < code.Length; i++)
                {
                    if (result[i] != code[i])
                    {
                        return false;
                    }
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Create a command with a code and an int values
        /// </summary>
        /// <param name="command">the 4 byte command (STAT, RECV, ...).</param>
        /// <param name="value"></param>
        /// <returns>the byte[] to send to the device through adb</returns>
        private static byte[] CreateRequest(string command, int value)
        {
            return CreateRequest(Encoding.Default.GetBytes(command), value);
        }

        /// <summary>
        /// Create a command with a code and an int values
        /// </summary>
        /// <param name="command">the 4 byte command (STAT, RECV, ...).</param>
        /// <param name="value"></param>
        /// <returns>the byte[] to send to the device through adb</returns>
        private static byte[] CreateRequest(byte[] command, int value)
        {
            byte[] array = new byte[8];

            Array.Copy(command, 0, array, 0, 4);
            value.Swap32bitsToArray(array, 4);

            return array;
        }

        /// <summary>
        /// Creates the data array for a file request. This creates an array with a 4 byte command + the remote file name.
        /// </summary>
        /// <param name="command">the 4 byte command (STAT, RECV, ...).</param>
        /// <param name="path">The path, as a byte array, of the remote file on which to execute the command</param>
        /// <returns>the byte[] to send to the device through adb</returns>
        private static byte[] CreateFileRequest(string command, string path)
        {
            return CreateFileRequest(Encoding.Default.GetBytes(command), Encoding.Default.GetBytes(path));
        }

        /// <summary>
        /// Creates the data array for a file request. This creates an array with a 4 byte command + the remote file name.
        /// </summary>
        /// <param name="command">the 4 byte command (STAT, RECV, ...).</param>
        /// <param name="path">he path, as a byte array, of the remote file on which to execute the command</param>
        /// <returns>the byte[] to send to the device through adb</returns>
        private static byte[] CreateFileRequest(byte[] command, byte[] path)
        {
            byte[] array = new byte[8 + path.Length];

            Array.Copy(command, 0, array, 0, 4);
            path.Length.Swap32bitsToArray(array, 4);
            Array.Copy(path, 0, array, 8, path.Length);

            return array;
        }

        private static byte[] CreateSendFileRequest(string command, string path, FileMode mode)
        {
            return CreateSendFileRequest(Encoding.Default.GetBytes(command), Encoding.Default.GetBytes(path), mode);
        }

        private static byte[] CreateSendFileRequest(byte[] command, byte[] path, FileMode mode)
        {
            string modeString = string.Format(",{0}", ((int)mode & 0777));
            byte[] modeContent = null;
            try
            {
                modeContent = Encoding.Default.GetBytes(modeString);
            }
            catch (EncoderFallbackException)
            {
                return null;
            }

            byte[] array = new byte[8 + path.Length + modeContent.Length];
            Array.Copy(command, 0, array, 0, 4);
            (path.Length + modeContent.Length).Swap32bitsToArray(array, 4);
            Array.Copy(path, 0, array, 8, path.Length);
            Array.Copy(modeContent, 0, array, 8 + path.Length, modeContent.Length);

            return array;
        }
        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncService"/> class.
        /// </summary>
        /// <param name="device">The device.</param>
        public SyncService(Device device)
            : this(AndroidDebugBridge.SocketAddress, device)
            {

        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SyncService"/> class.
        /// </summary>
        /// <param name="address">The address.</param>
        /// <param name="device">The device.</param>
        public SyncService(IPEndPoint address, Device device)
        {
            this.Address = address;
            this.Device = device;
            this.Open();
        }

        /// <summary>
        /// Gets the address.
        /// </summary>
        /// <value>
        /// The address.
        /// </value>
        public IPEndPoint Address { get; private set; }
        /// <summary>
        /// Gets the device.
        /// </summary>
        /// <value>
        /// The device.
        /// </value>
        public Device Device { get; private set; }
        private Socket Channel { get; set; }

        /// <include file='.\ISyncService.xml' path='/SyncService/IsOpen/*'/>
        public bool IsOpen
        {
            get
            {
                return this.Channel != null && this.Channel.Connected;
            }
        }
        
        /// <include file='.\ISyncService.xml' path='/SyncService/Open/*'/>
        public bool Open()
        {
            if (this.IsOpen)
            {
                return true;
            }

            try
            {
                this.Channel = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                this.Channel.Connect(this.Address);
                this.Channel.Blocking = true;

                // target a specific device
                AdbHelper.Instance.SetDevice(this.Channel, this.Device);

                byte[] request = AdbHelper.Instance.FormAdbRequest("sync:");
                AdbHelper.Instance.Write(this.Channel, request, -1, DdmPreferences.Timeout);

                AdbResponse resp = AdbHelper.Instance.ReadAdbResponse(this.Channel, false /* readDiagString */);

                if (!resp.IOSuccess || !resp.Okay)
                {
                    Log.w("ddms:syncservice", "Got timeout or unhappy response from ADB sync req: {0}", resp.Message);
                    this.Channel.Close();
                    this.Channel = null;
                    return false;
                }
            }
            catch (IOException)
            {
                this.Close();
                throw;
            }

            return true;
        }

        /// <include file='.\ISyncService.xml' path='/SyncService/Close/*'/>
        public void Close()
        {
            if (this.Channel != null)
            {
                try
                {
                    this.Channel.Close();
                }
                catch (IOException)
                {
                    
                }
                this.Channel = null;
            }
        }

        /// <include file='.\ISyncService.xml' path='/SyncService/PullFile2/*'/>
        public SyncResult PullFile(string remoteFilepath, string localFilename, ISyncProgressMonitor monitor)
        {
            if (monitor == null)
            {
                throw new ArgumentNullException("monitor", "Monitor cannot be null");
            }

            long totalWork = 0;
            monitor.Start(totalWork);

            SyncResult result = this.DoPullFile(remoteFilepath, localFilename, monitor);

            monitor.Stop();
            return result;
        }

        /// <include file='.\ISyncService.xml' path='/SyncService/PushFile/*'/>
        public SyncResult PushFile(string local, string remote, ISyncProgressMonitor monitor)
        {
            if (monitor == null)
            {
                throw new ArgumentNullException("monitor", "Monitor cannot be null");
            }

            FileInfo f = new FileInfo(local);
            if (!f.Exists)
            {
                return new SyncResult(ErrorCodeHelper.RESULT_NO_LOCAL_FILE);
            }

            if (f.IsDirectory())
            {
                return new SyncResult(ErrorCodeHelper.RESULT_LOCAL_IS_DIRECTORY);
            }

            monitor.Start(f.Length);
            SyncResult result = this.DoPushFile(local, remote, monitor);
            monitor.Stop();

            return result;
        }

        /// <summary>
        /// Push a single file
        /// </summary>
        /// <param name="local">the local file to push</param>
        /// <param name="remotePath">the remote file (length max is 1024)</param>
        /// <param name="monitor">the monitor. The monitor must be started already.</param>
        /// <returns>
        /// a SyncResult object with a code and an optional message.
        /// </returns>
        /// <exception cref="System.ArgumentNullException">monitor;Monitor cannot be null</exception>
        /// <exception cref="ArgumentNullException">Throws if monitor is null</exception>
        private SyncResult DoPushFile(string local, string remotePath, ISyncProgressMonitor monitor)
        {
            if (monitor == null)
            {
                throw new ArgumentNullException("monitor", "Monitor cannot be null");
            }

            FileStream fs = null;
            byte[] msg;

            int timeOut = DdmPreferences.Timeout;
            Log.i(TAG, "Remote File: {0}", remotePath);
            try
            {
                byte[] remotePathContent = remotePath.GetBytes(AdbHelper.DEFAULT_ENCODING);

                if (remotePathContent.Length > REMOTE_PATH_MAX_LENGTH)
                {
                    return new SyncResult(ErrorCodeHelper.RESULT_REMOTE_PATH_LENGTH);
                }

                // this shouldn't happen but still...
                if (!File.Exists(local))
                {
                    return new SyncResult(ErrorCodeHelper.RESULT_NO_LOCAL_FILE);
                }

                // create the stream to read the file
                fs = new FileStream(local, System.IO.FileMode.Open, FileAccess.Read);

                // create the header for the action
                msg = CreateSendFileRequest(SEND.GetBytes(), remotePathContent, (FileMode)0644);
            }
            catch (EncoderFallbackException e)
            {
                return new SyncResult(ErrorCodeHelper.RESULT_REMOTE_PATH_ENCODING, e);
            }
            catch (FileNotFoundException e)
            {
                return new SyncResult(ErrorCodeHelper.RESULT_FILE_READ_ERROR, e);
            }

            // and send it. We use a custom try/catch block to make the difference between
            // file and network IO exceptions.
            try
            {
                AdbHelper.Instance.Write(this.Channel, msg, -1, timeOut);
            }
            catch (IOException e)
            {
                return new SyncResult(ErrorCodeHelper.RESULT_CONNECTION_ERROR, e);
            }

            // create the buffer used to read.
            // we read max SYNC_DATA_MAX, but we need 2 4 bytes at the beginning.
            if (DataBuffer == null)
            {
                DataBuffer = new byte[SYNC_DATA_MAX + 8];
            }
            byte[] bDATA = DATA.GetBytes();
            Array.Copy(bDATA, 0, DataBuffer, 0, bDATA.Length);

            // look while there is something to read
            while (true)
            {
                // check if we're canceled
                if (monitor.IsCanceled)
                {
                    return new SyncResult(ErrorCodeHelper.RESULT_CANCELED);
                }

                // read up to SYNC_DATA_MAX
                int readCount = 0;
                try
                {
                    readCount = fs.Read(DataBuffer, 8, SYNC_DATA_MAX);
                }
                catch (IOException e)
                {
                    return new SyncResult(ErrorCodeHelper.RESULT_FILE_READ_ERROR, e);
                }

                if (readCount == 0)
                {
                    // we reached the end of the file
                    break;
                }

                // now send the data to the device
                // first write the amount read
                readCount.Swap32bitsToArray(DataBuffer, 4);

                // now write it
                try
                {
                    AdbHelper.Instance.Write(this.Channel, DataBuffer, readCount + 8, timeOut);
                }
                catch (IOException e)
                {
                    return new SyncResult(ErrorCodeHelper.RESULT_CONNECTION_ERROR, e);
                }

                // and advance the monitor
                monitor.Advance(readCount);
            }
            // close the local file
            try
            {
                fs.Close();
            }
            catch (IOException e)
            {
                return new SyncResult(ErrorCodeHelper.RESULT_FILE_READ_ERROR, e);
            }

            try
            {
                // create the DONE message
                long time = DateTime.Now.CurrentTimeMillis() / 1000;
                msg = CreateRequest(DONE, (int)time);

                // and send it.
                AdbHelper.Instance.Write(this.Channel, msg, -1, timeOut);

                // read the result, in a byte array containing 2 ints
                // (id, size)
                byte[] result = new byte[8];
                AdbHelper.Instance.Read(this.Channel, result, -1 /* full length */, timeOut);

                if (!CheckResult(result, OKAY.GetBytes()))
                {
                    if (CheckResult(result, FAIL.GetBytes()))
                    {
                        // read some error message...
                        int len = result.Swap32bitFromArray(4);

                        AdbHelper.Instance.Read(this.Channel, DataBuffer, len, timeOut);

                        // output the result?
                        string message = DataBuffer.GetString(0, len);
                        Log.e("ddms", "transfer error: " + message);
                        return new SyncResult(ErrorCodeHelper.RESULT_UNKNOWN_ERROR, message);
                    }

                    return new SyncResult(ErrorCodeHelper.RESULT_UNKNOWN_ERROR);
                }
            }
            catch (IOException e)
            {
                return new SyncResult(ErrorCodeHelper.RESULT_CONNECTION_ERROR, e);
            }

            return new SyncResult(ErrorCodeHelper.RESULT_OK);
        }

        public SyncResult DoPush(IEnumerable<FileSystemInfo> files, string remotePath, ISyncProgressMonitor monitor)
        {
            if (monitor == null)
            {
                throw new ArgumentNullException("monitor", "Monitor cannot be null");
            }

            // check if we're canceled
            if (monitor.IsCanceled)
            {
                return new SyncResult(ErrorCodeHelper.RESULT_CANCELED);
            }

            foreach (FileSystemInfo f in files)
            {
                // check if we're canceled
                if (monitor.IsCanceled)
                {
                    return new SyncResult(ErrorCodeHelper.RESULT_CANCELED);
                }
                // append the name of the directory/file to the remote path
                string dest = LinuxPath.Combine(remotePath, f.Name);
                if (f.Exists)
                {
                    if (f.IsDirectory())
                    {
                        DirectoryInfo fsiDir = f as DirectoryInfo;
                        monitor.StartSubTask(f.FullName, dest);
                        SyncResult result = this.DoPush(fsiDir.GetFileSystemInfos(), dest, monitor);

                        if (result.Code != ErrorCodeHelper.RESULT_OK)
                        {
                            return result;
                        }

                        monitor.Advance(1);
                    }
                    else if (f.IsFile())
                    {
                        monitor.StartSubTask(f.FullName, dest);
                        SyncResult result = this.DoPushFile(f.FullName, dest, monitor);
                        if (result.Code != ErrorCodeHelper.RESULT_OK)
                        {
                            return result;
                        }
                    }
                }
            }

            return new SyncResult(ErrorCodeHelper.RESULT_OK);
        }

        /// <summary>
        /// Pulls a remote file
        /// </summary>
        /// <param name="remotePath">the remote file (length max is 1024)</param>
        /// <param name="localPath">the local destination</param>
        /// <param name="monitor">the monitor. The monitor must be started already.</param>
        /// <returns>a SyncResult object with a code and an optional message.</returns>
        /// <exception cref="ArgumentNullException">Throws if monitor is null</exception>
        public SyncResult DoPullFile(string remotePath, string localPath, ISyncProgressMonitor monitor)
        {
            if (monitor == null)
            {
                throw new ArgumentNullException("monitor", "Monitor cannot be null");
            }

            byte[] msg = null;
            byte[] pullResult = new byte[8];

            int timeOut = DdmPreferences.Timeout;

            try
            {
                byte[] remotePathContent = remotePath.GetBytes(AdbHelper.DEFAULT_ENCODING);

                if (remotePathContent.Length > REMOTE_PATH_MAX_LENGTH)
                {
                    return new SyncResult(ErrorCodeHelper.RESULT_REMOTE_PATH_LENGTH);
                }

                // create the full request message
                msg = CreateFileRequest(RECV.GetBytes(), remotePathContent);

                // and send it.
                AdbHelper.Instance.Write(this.Channel, msg, -1, timeOut);

                // read the result, in a byte array containing 2 ints
                // (id, size)
                AdbHelper.Instance.Read(this.Channel, pullResult, -1, timeOut);

                // check we have the proper data back
                if (CheckResult(pullResult, DATA.GetBytes()) == false &&
                                CheckResult(pullResult, DONE.GetBytes()) == false)
                                {
                    return new SyncResult(ErrorCodeHelper.RESULT_CONNECTION_ERROR);
                }
            }
            catch (EncoderFallbackException e)
            {
                Log.e(TAG, e);
                return new SyncResult(ErrorCodeHelper.RESULT_REMOTE_PATH_ENCODING, e);
            }
            catch (IOException e)
            {
                Log.e(TAG, e);
                return new SyncResult(ErrorCodeHelper.RESULT_CONNECTION_ERROR, e);
            }

            // access the destination file
            FileInfo f = new FileInfo(localPath);

            // create the stream to write in the file. We use a new try/catch block to differentiate
            // between file and network io exceptions.
            FileStream fos = null;
            try
            {
                fos = new FileStream(f.FullName,System.IO.FileMode.Create,FileAccess.Write);
            }
            catch (FileNotFoundException e)
            {
                return new SyncResult(ErrorCodeHelper.RESULT_FILE_WRITE_ERROR, e);
            }

            // the buffer to read the data
            byte[] data = new byte[SYNC_DATA_MAX];
            using (fos)
            {
                // loop to get data until we're done.
                while (true)
                {
                    // check if we're canceled
                    if (monitor.IsCanceled)
                    {
                        return new SyncResult(ErrorCodeHelper.RESULT_CANCELED);
                    }

                    // if we're done, we stop the loop
                    if (CheckResult(pullResult, DONE.GetBytes()))
                    {
                        break;
                    }
                    if (CheckResult(pullResult, DATA.GetBytes()) == false)
                    {
                        // hmm there's an error
                        return new SyncResult(ErrorCodeHelper.RESULT_CONNECTION_ERROR);
                    }
                    int length = pullResult.Swap32bitFromArray(4);
                    if (length > SYNC_DATA_MAX)
                    {
                        // buffer overrun!
                        // error and exit
                        return new SyncResult(ErrorCodeHelper.RESULT_BUFFER_OVERRUN);
                    }

                    try
                    {
                        // now read the length we received
                        AdbHelper.Instance.Read(this.Channel, data, length, timeOut);

                        // get the header for the next packet.
                        AdbHelper.Instance.Read(this.Channel, pullResult, -1, timeOut);
                    }
                    catch (IOException e)
                    {
                        Log.e(TAG, e);
                        return new SyncResult(ErrorCodeHelper.RESULT_CONNECTION_ERROR, e);
                    }
                    // write the content in the file
                    try
                    {
                        fos.Write(data, 0, length);
                    }
                    catch (IOException e)
                    {
                        return new SyncResult(ErrorCodeHelper.RESULT_FILE_WRITE_ERROR, e);
                    }

                    monitor.Advance(length);
                }

                try
                {
                    fos.Flush();
                }
                catch (IOException e)
                {
                    Log.e(TAG, e);
                    return new SyncResult(ErrorCodeHelper.RESULT_FILE_WRITE_ERROR, e);
                }
            }
            return new SyncResult(ErrorCodeHelper.RESULT_OK);
        }

        /// <summary>
        /// compute the recursive file size of all the files in the list. Folders have a weight of 1.
        /// </summary>
        /// <param name="files">The local files / folders</param>
        /// <returns>The total number of bytes</returns>
        /// <remarks>This does not check for circular links.</remarks>
        public long GetTotalLocalFileSize(IEnumerable<FileSystemInfo> fsis)
        {
            long count = 0;

            foreach (FileSystemInfo fsi in fsis)
            {
                if (fsi.Exists)
                {
                    if (fsi is DirectoryInfo)
                    {
                        return this.GetTotalLocalFileSize((fsi as DirectoryInfo).GetFileSystemInfos()) + 1;
                    }
                    else if (fsi is FileInfo)
                    {
                        count += (fsi as FileInfo).Length;
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Returns the mode of the remote file.
        /// </summary>
        /// <param name="path">the remote file</param>
        /// <returns>the mode if all went well; otherwise, FileMode.UNKNOWN</returns>
        private FileMode ReadMode(string path)
        {
            try
            {
                // create the stat request message.
                byte[] msg = CreateFileRequest(STAT, path);

                AdbHelper.Instance.Write(this.Channel, msg, -1 /* full length */, DdmPreferences.Timeout);

                // read the result, in a byte array containing 4 ints
                // (id, mode, size, time)
                byte[] statResult = new byte[16];
                AdbHelper.Instance.Read(this.Channel, statResult, -1 /* full length */, DdmPreferences.Timeout);

                // check we have the proper data back
                if (CheckResult(statResult, Encoding.Default.GetBytes(STAT)) == false)
                {
                    return FileMode.UNKNOWN;
                }

                // we return the mode (2nd int in the array)
                return (FileMode)statResult.Swap32bitFromArray(4);
            }
            catch (IOException e)
            {
                Log.w("SyncService", e);
                return FileMode.UNKNOWN;
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            this.Close();
        }
    }
}
