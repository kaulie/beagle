/*
 * Copyright 2005 The Apache Software Foundation
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 * http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;
namespace Lucene.Net.Store
{
	
    /// <summary>Base implementation class for buffered {@link IndexOutput}. </summary>
    public abstract class BufferedIndexOutput:IndexOutput
    {
        internal const int BUFFER_SIZE = 1024;
		
        private byte[] buffer = new byte[BUFFER_SIZE];
        private long bufferStart = 0; // position in file of buffer
        private int bufferPosition = 0; // position in buffer
		
        /// <summary>Writes a single byte.</summary>
        /// <seealso cref="IndexInput#ReadByte()">
        /// </seealso>
        public override void  WriteByte(byte b)
        {
            if (bufferPosition >= BUFFER_SIZE)
                Flush();
            buffer[bufferPosition++] = b;
        }
		
        /// <summary>Writes an array of bytes.</summary>
        /// <param name="b">the bytes to write
        /// </param>
        /// <param name="length">the number of bytes to write
        /// </param>
        /// <seealso cref="IndexInput#ReadBytes(byte[],int,int)">
        /// </seealso>
        public override void  WriteBytes(byte[] b, int length)
        {
            for (int i = 0; i < length; i++)
                WriteByte(b[i]);
        }
		
        /// <summary>Forces any buffered output to be written. </summary>
        public override void  Flush()
        {
            FlushBuffer(buffer, bufferPosition);
            bufferStart += bufferPosition;
            bufferPosition = 0;
        }
		
        /// <summary>Expert: implements buffer write.  Writes bytes at the current position in
        /// the output.
        /// </summary>
        /// <param name="b">the bytes to write
        /// </param>
        /// <param name="len">the number of bytes to write
        /// </param>
        public abstract void  FlushBuffer(byte[] b, int len);
		
        /// <summary>Closes this stream to further operations. </summary>
        public override void  Close()
        {
            Flush();
        }
		
        /// <summary>Returns the current position in this file, where the next write will
        /// occur.
        /// </summary>
        /// <seealso cref="#Seek(long)">
        /// </seealso>
        public override long GetFilePointer()
        {
            return bufferStart + bufferPosition;
        }
		
        /// <summary>Sets current position in this file, where the next write will occur.</summary>
        /// <seealso cref="#GetFilePointer()">
        /// </seealso>
        public override void  Seek(long pos)
        {
            Flush();
            bufferStart = pos;
        }
		
        /// <summary>The number of bytes in the file. </summary>
        public abstract override long Length();
    }
}