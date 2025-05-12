import React, { useState, useRef, useCallback } from 'react';
import { Upload, X, Check, Loader2, FileVideo } from 'lucide-react';
import { useFFmpeg } from '../hooks/useFFmpeg';

const FileUploader: React.FC = () => {
  const [file, setFile] = useState<File | null>(null);
  const [isDragging, setIsDragging] = useState(false);
  const [videoUrl, setVideoUrl] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  
  const { 
    isReady, 
    isLoading: isProcessing, 
    progress, 
    audioUrl, 
    extractAudio, 
    error: ffmpegError 
  } = useFFmpeg();

  const handleFileChange = useCallback((selectedFile: File | null) => {
    setError(null);
    
    if (!selectedFile) {
      setFile(null);
      setVideoUrl(null);
      return;
    }

    // Validate file type
    if (!selectedFile.type.includes('video/mp4')) {
      setError('Please upload an MP4 file');
      return;
    }

    setFile(selectedFile);
    
    // Create URL for video preview
    const url = URL.createObjectURL(selectedFile);
    setVideoUrl(url);
  }, []);

  const handleDragOver = useCallback((e: React.DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setIsDragging(true);
  }, []);

  const handleDragLeave = useCallback(() => {
    setIsDragging(false);
  }, []);

  const handleDrop = useCallback((e: React.DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setIsDragging(false);
    
    if (e.dataTransfer.files && e.dataTransfer.files.length > 0) {
      handleFileChange(e.dataTransfer.files[0]);
    }
  }, [handleFileChange]);

  const handleFileInputChange = useCallback((e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files && e.target.files.length > 0) {
      handleFileChange(e.target.files[0]);
    }
  }, [handleFileChange]);

  const triggerFileInput = useCallback(() => {
    if (fileInputRef.current) {
      fileInputRef.current.click();
    }
  }, []);

  const handleRemoveFile = useCallback(() => {
    setFile(null);
    setVideoUrl(null);
    if (fileInputRef.current) {
      fileInputRef.current.value = '';
    }
  }, []);

  const handleConvert = useCallback(async () => {
    if (!file) return;
    
    try {
      await extractAudio(file);
    } catch (err) {
      setError('An error occurred during conversion');
      console.error(err);
    }
  }, [file, extractAudio]);

  return (
    <div className="space-y-6">
      {!file ? (
        <div
          className={`border-2 border-dashed rounded-lg p-8 text-center cursor-pointer transition-colors duration-200 ${
            isDragging
              ? 'border-purple-500 bg-purple-50'
              : 'border-gray-300 hover:border-purple-500 hover:bg-purple-50'
          }`}
          onDragOver={handleDragOver}
          onDragLeave={handleDragLeave}
          onDrop={handleDrop}
          onClick={triggerFileInput}
        >
          <input
            type="file"
            accept="video/mp4"
            onChange={handleFileInputChange}
            ref={fileInputRef}
            className="hidden"
          />
          <div className="flex flex-col items-center">
            <Upload className="h-12 w-12 text-purple-500 mb-3" />
            <p className="text-lg font-medium mb-1">
              {isDragging ? 'Drop your MP4 file here' : 'Drag & drop your MP4 file here'}
            </p>
            <p className="text-sm text-gray-500 mb-3">or click to browse files</p>
            <p className="text-xs text-gray-400">MP4 files only</p>
          </div>
        </div>
      ) : (
        <div className="space-y-4">
          <div className="flex items-center justify-between">
            <div className="flex items-center">
              <div className="mr-3">
                <div className="h-12 w-12 bg-purple-100 rounded-lg flex items-center justify-center">
                  <FileVideo className="h-6 w-6 text-purple-600" />
                </div>
              </div>
              <div className="overflow-hidden">
                <p className="font-medium truncate">{file.name}</p>
                <p className="text-sm text-gray-500">{(file.size / (1024 * 1024)).toFixed(2)} MB</p>
              </div>
            </div>
            <button
              onClick={handleRemoveFile}
              className="ml-2 p-1 text-gray-400 hover:text-red-500 transition-colors duration-200"
              aria-label="Remove file"
            >
              <X className="h-5 w-5" />
            </button>
          </div>

          {videoUrl && (
            <div className="rounded-lg overflow-hidden bg-black">
              <video
                src={videoUrl}
                controls
                className="w-full h-auto max-h-60 object-contain"
              />
            </div>
          )}

          {!isReady ? (
            <div className="text-center py-4">
              <p className="text-gray-600">Loading FFmpeg.wasm...</p>
              <div className="mt-2">
                <Loader2 className="h-6 w-6 animate-spin text-purple-600 mx-auto" />
              </div>
            </div>
          ) : isProcessing ? (
            <div className="py-4">
              <div className="flex items-center">
                <div className="flex-grow">
                  <div className="h-2 rounded-full bg-gray-200 overflow-hidden">
                    <div
                      className="h-full bg-purple-600 transition-all duration-300"
                      style={{ width: `${progress}%` }}
                    ></div>
                  </div>
                </div>
                <span className="ml-2 text-sm font-medium text-gray-600">{progress}%</span>
              </div>
              <p className="text-center mt-2 text-gray-600">Extracting audio...</p>
            </div>
          ) : audioUrl ? (
            <div className="py-4 text-center">
              <div className="mb-4 inline-flex items-center text-green-600 bg-green-100 px-3 py-1 rounded-full">
                <Check className="h-4 w-4 mr-1" />
                <span className="text-sm font-medium">Conversion completed</span>
              </div>

              <audio controls className="w-full">
                <source src={audioUrl} type="audio/mpeg" />
                Your browser does not support the audio element.
              </audio>

              <div className="mt-4">
                <a
                  href={audioUrl}
                  download={`${file.name.replace(/\.[^/.]+$/, '')}.mp3`}
                  className="inline-flex items-center justify-center px-4 py-2 border border-transparent text-sm font-medium rounded-md shadow-sm text-white bg-purple-600 hover:bg-purple-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-purple-500 transition-colors duration-200"
                >
                  Download MP3
                </a>
              </div>
            </div>
          ) : (
            <div className="py-4 text-center">
              <button
                onClick={handleConvert}
                className="inline-flex items-center justify-center px-4 py-2 border border-transparent text-sm font-medium rounded-md shadow-sm text-white bg-purple-600 hover:bg-purple-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-purple-500 transition-colors duration-200"
              >
                Extract Audio
              </button>
            </div>
          )}
        </div>
      )}

      {(error || ffmpegError) && (
        <div className="text-red-500 text-sm bg-red-50 p-3 rounded-md border border-red-200">
          {error || ffmpegError}
        </div>
      )}
    </div>
  );
};

export default FileUploader;