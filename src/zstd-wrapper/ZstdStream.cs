namespace System.IO.Compression.Zstd;

using System.Buffers;
using System.Runtime.InteropServices;


/// <summary>
/// Zstd Stream
/// </summary>
public class ZstdStream : Stream
{
    private Stream stream;
    private CompressionMode mode;
    private Boolean leaveOpen;
    private Boolean isClosed = false;
    private Boolean isDisposed = false;
    private Boolean isInitialized = false;

    private IntPtr zstream;
    private uint zstreamInputSize;
    private uint zstreamOutputSize;

    private byte[]? data;
    private bool dataDepleted = false;
    private bool dataSkipRead = false;
    private int dataPosition = 0;
    private int dataSize = 0;

    private Buffer outputBuffer = new Buffer();
    private Buffer inputBuffer = new Buffer();
    private ArrayPool<byte> arrayPool = ArrayPool<byte>.Shared;

    /// <summary>
    ///  class using the specified stream and compression mode, and optionally leaves the stream open.
    /// </summary>
    /// <param name="stream">The stream to compress or decompress.</param>
    /// <param name="mode">compress stream open mode</param>
    /// <param name="leaveOpen">inner stream operation after dispose current stream</param>
    /// <exception cref="ArgumentNullException"></exception>
    public ZstdStream(Stream stream, CompressionMode mode = CompressionMode.Compress, bool leaveOpen = false)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        this.mode = mode;
        this.leaveOpen = leaveOpen;

        if (mode == CompressionMode.Compress)
        {
            this.zstreamInputSize = Interop.ZSTD_CStreamInSize().ToUInt32();
            this.zstreamOutputSize = Interop.ZSTD_CStreamOutSize().ToUInt32();
            this.zstream = Interop.ZSTD_createCStream();
            this.data = arrayPool.Rent((int)this.zstreamOutputSize);
        }

        if (mode == CompressionMode.Decompress)
        {
            this.zstreamInputSize = Interop.ZSTD_DStreamInSize().ToUInt32();
            this.zstreamOutputSize = Interop.ZSTD_DStreamOutSize().ToUInt32();
            this.zstream = Interop.ZSTD_createDStream();
            this.data = arrayPool.Rent((int)this.zstreamInputSize);
        }
    }

    /// <summary>
    ///  class using the specified stream and compression mode, and optionally leaves the stream open.
    /// </summary>
    /// <param name="stream">The stream to compress.</param>
    /// <param name="leaveOpen">inner stream operation after dispose current stream</param>
    /// <exception cref="ArgumentNullException"></exception>
    public ZstdStream(Stream stream, int compressionLevel, bool leaveOpen = false) : this(stream, CompressionMode.Compress, leaveOpen)
    {
        this.CompressionLevel = compressionLevel;
    }





    /// <summary>
    /// Gets or sets the compression level to use, the default is 6.
    /// </summary>
    public int CompressionLevel { get; set; } = 6;

    public override bool CanRead => this.stream.CanRead && this.mode == CompressionMode.Decompress;

    public override bool CanWrite => this.stream.CanWrite && this.mode == CompressionMode.Compress;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (this.isDisposed == false)
        {
            if (!this.isClosed)
            {
                ReleaseResources(flushStream: false);
            }
            if (this.data != null)
            {
                this.arrayPool.Return(this.data, clearArray: false);
            }
            this.isDisposed = true;
            this.data = null;
        }
    }

    public override void Close()
    {
        if (this.isClosed) return;

        try
        {
            ReleaseResources(flushStream: true);
        }
        finally
        {
            this.isClosed = true;
            base.Close();
        }
    }

    private void ReleaseResources(bool flushStream)
    {
        if (this.mode == CompressionMode.Compress)
        {
            try
            {
                if (flushStream)
                {
                    this.FlushStream((zcs, buffer) => Interop.ThrowIfError(Interop.ZSTD_flushStream(zcs, buffer)));
                    this.FlushStream((zcs, buffer) => Interop.ThrowIfError(Interop.ZSTD_endStream(zcs, buffer)));
                    this.stream.Flush();
                }
            }
            finally
            {
                Interop.ZSTD_freeCStream(this.zstream);
                if (!this.leaveOpen) this.stream.Close();
            }
        }
        else if (this.mode == CompressionMode.Decompress)
        {
            Interop.ZSTD_freeDStream(this.zstream);
            if (!this.leaveOpen) this.stream.Close();
        }
    }

    public override void Flush()
    {
        if (this.mode == CompressionMode.Compress)
        {
            this.FlushStream((zcs, buffer) => Interop.ThrowIfError(Interop.ZSTD_flushStream(zcs, buffer)));
            this.stream.Flush();
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (this.CanRead == false) throw new NotSupportedException();

        // prevent the buffers from being moved around by the garbage collector
        var alloc1 = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        var alloc2 = GCHandle.Alloc(this.data, GCHandleType.Pinned);

        try
        {
            var length = 0;

            if (this.isInitialized == false)
            {
                this.isInitialized = true;

                var result = Interop.ZSTD_initDStream(this.zstream);
            }

            while (count > 0)
            {
                var inputSize = this.dataSize - this.dataPosition;

                // read data from input stream 
                if (inputSize <= 0 && !this.dataDepleted && !this.dataSkipRead)
                {
                    this.dataSize = this.stream.Read(this.data, 0, (int)this.zstreamInputSize);
                    this.dataDepleted = this.dataSize <= 0;
                    this.dataPosition = 0;
                    inputSize = this.dataDepleted ? 0 : this.dataSize;
                    this.dataSkipRead = true;
                }
                // inputBuffer
                this.inputBuffer.Data = inputSize <= 0 ? IntPtr.Zero : Marshal.UnsafeAddrOfPinnedArrayElement(this.data, this.dataPosition);
                this.inputBuffer.Size = inputSize <= 0 ? UIntPtr.Zero : new UIntPtr((uint)inputSize);
                this.inputBuffer.Position = UIntPtr.Zero;

                // outputBuffer
                this.outputBuffer.Data = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, offset);
                this.outputBuffer.Size = new UIntPtr((uint)count);
                this.outputBuffer.Position = UIntPtr.Zero;

                // decompress inputBuffer => outputBuffer
                Interop.ThrowIfError(Interop.ZSTD_decompressStream(this.zstream, this.outputBuffer, this.inputBuffer));

                // progress in outputBuffer
                var outputBufferPosition = (int)this.outputBuffer.Position.ToUInt32();
                if (outputBufferPosition == 0)
                {
                    // the internal buffer is depleted, we're either done
                    if (this.dataDepleted) break;

                    // or we need more bytes
                    this.dataSkipRead = false;
                }
                length += outputBufferPosition;
                offset += outputBufferPosition;
                count -= outputBufferPosition;

                // calculate progress in inputBuffer
                var inputBufferPosition = (int)inputBuffer.Position.ToUInt32();
                this.dataPosition += inputBufferPosition;
            }

            return length;
        }
        finally
        {
            alloc1.Free();
            alloc2.Free();
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (this.CanWrite == false) throw new NotSupportedException();

        // prevent the buffers from being moved around by the garbage collector
        var alloc1 = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        var alloc2 = GCHandle.Alloc(this.data, GCHandleType.Pinned);

        try
        {
            if (this.isInitialized == false)
            {
                this.isInitialized = true;

                var result = Interop.ZSTD_initCStream(this.zstream, this.CompressionLevel);

                Interop.ThrowIfError(result);
            }

            while (count > 0)
            {
                var inputSize = Math.Min((uint)count, this.zstreamInputSize);

                // configure the outputBuffer
                this.outputBuffer.Data = Marshal.UnsafeAddrOfPinnedArrayElement(this.data, 0);
                this.outputBuffer.Size = new UIntPtr(this.zstreamOutputSize);
                this.outputBuffer.Position = UIntPtr.Zero;

                // configure the inputBuffer
                this.inputBuffer.Data = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, offset);
                this.inputBuffer.Size = new UIntPtr((uint)inputSize);
                this.inputBuffer.Position = UIntPtr.Zero;

                // compress inputBuffer to outputBuffer
                Interop.ThrowIfError(Interop.ZSTD_compressStream(this.zstream, this.outputBuffer, this.inputBuffer));

                // write data to output stream
                var outputBufferPosition = (int)this.outputBuffer.Position.ToUInt32();
                this.stream.Write(this.data, 0, outputBufferPosition);

                // calculate progress in inputBuffer
                var inputBufferPosition = (int)this.inputBuffer.Position.ToUInt32();
                offset += inputBufferPosition;
                count -= inputBufferPosition;
            }
        }
        finally
        {
            alloc1.Free();
            alloc2.Free();
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotImplementedException();
    }

    public override void SetLength(long value)
    {
        throw new NotImplementedException();
    }

    private void FlushStream(Action<IntPtr, Buffer> outputAction)
    {
        ArgumentNullException.ThrowIfNull(data);
        var alloc = GCHandle.Alloc(this.data, GCHandleType.Pinned);

        try
        {
            this.outputBuffer.Data = Marshal.UnsafeAddrOfPinnedArrayElement(this.data, 0);
            this.outputBuffer.Size = new UIntPtr(this.zstreamOutputSize);
            this.outputBuffer.Position = UIntPtr.Zero;

            outputAction(this.zstream, this.outputBuffer);

            var outputBufferPosition = (int)this.outputBuffer.Position.ToUInt32();
            this.stream.Write(this.data, 0, outputBufferPosition);
        }
        finally
        {
            alloc.Free();
        }
    }



    internal static class Interop
    {
        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        public static extern uint ZSTD_versionNumber();

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        public static extern int ZSTD_maxCLevel();

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ZSTD_createCStream();

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_initCStream(IntPtr zcs, int compressionLevel);

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_freeCStream(IntPtr zcs);

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_CStreamInSize();

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_CStreamOutSize();

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_compressStream(IntPtr zcs, [MarshalAs(UnmanagedType.LPStruct)] Buffer outputBuffer, [MarshalAs(UnmanagedType.LPStruct)] Buffer inputBuffer);

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ZSTD_createDStream();

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_initDStream(IntPtr zds);

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_freeDStream(IntPtr zds);

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_DStreamInSize();

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_DStreamOutSize();

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_decompressStream(IntPtr zds, [MarshalAs(UnmanagedType.LPStruct)] Buffer outputBuffer, [MarshalAs(UnmanagedType.LPStruct)] Buffer inputBuffer);

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_flushStream(IntPtr zcs, [MarshalAs(UnmanagedType.LPStruct)] Buffer outputBuffer);

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        public static extern UIntPtr ZSTD_endStream(IntPtr zcs, [MarshalAs(UnmanagedType.LPStruct)] Buffer outputBuffer);

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        internal static extern bool ZSTD_isError(UIntPtr code);

        [DllImport("libzstd", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ZSTD_getErrorName(UIntPtr code);

        public static void ThrowIfError(UIntPtr code)
        {
            if (ZSTD_isError(code))
            {
                var errorPtr = ZSTD_getErrorName(code);
                var errorMsg = Marshal.PtrToStringAnsi(errorPtr);
                throw new IOException(errorMsg);
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    public class Buffer
    {
        public IntPtr Data = IntPtr.Zero;
        public UIntPtr Size = UIntPtr.Zero;
        public UIntPtr Position = UIntPtr.Zero;
    }

    public class ZstdProperties
    {
        /// <summary>
        /// The version of the native Zstd library.
        /// </summary>
        public static Version LibraryVersion => version.Value;

        /// <summary>
        /// The maximum compression level supported by the native Zstd library.
        /// </summary>
        public static int MaxCompressionLevel => maxCompressionLevel.Value;

        private static Lazy<Version> version = new Lazy<Version>(() =>
        {
            var version = (int)Interop.ZSTD_versionNumber();
            return new Version((version / 10000) % 100, (version / 100) % 100, version % 100);
        });

        private static Lazy<Int32> maxCompressionLevel = new Lazy<Int32>(() =>
        {
            return Interop.ZSTD_maxCLevel();
        });
    }
}
