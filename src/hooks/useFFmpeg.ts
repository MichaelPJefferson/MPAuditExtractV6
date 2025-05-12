import { useState, useEffect, useCallback } from 'react';
import { FFmpeg } from '@ffmpeg/ffmpeg';
import { toBlobURL, fetchFile } from '@ffmpeg/util';

export const useFFmpeg = () => {
  const [ffmpeg] = useState(() => new FFmpeg());
  const [isReady, setIsReady] = useState(false);
  const [isLoading, setIsLoading] = useState(false);
  const [progress, setProgress] = useState(0);
  const [audioUrl, setAudioUrl] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const load = async () => {
      try {
        // Set up log and progress handling
        ffmpeg.on('log', ({ message }) => {
          console.log(message);
        });

        ffmpeg.on('progress', ({ progress }) => {
          setProgress(Math.round(progress * 100));
        });

        // Load ffmpeg core from local public directory
        await ffmpeg.load({
          coreURL: '/ffmpeg-core.wasm'
        });

        setIsReady(true);
      } catch (err) {
        console.error('Error loading FFmpeg:', err);
        setError('Failed to load FFmpeg. Please try again or check your browser compatibility.');
      }
    };

    load();
  }, [ffmpeg]);

  const extractAudio = useCallback(
    async (videoFile: File) => {
      if (!isReady) {
        setError('FFmpeg is not ready yet. Please wait.');
        return;
      }

      try {
        setIsLoading(true);
        setProgress(0);
        setAudioUrl(null);
        setError(null);

        // Clear any previous file data
        await ffmpeg.writeFile('input.mp4', await fetchFile(videoFile));

        // Extract audio as MP3
        await ffmpeg.exec([
          '-i', 'input.mp4',
          '-vn', // No video
          '-acodec', 'libmp3lame', // MP3 codec
          '-q:a', '2', // Quality
          'output.mp3'
        ]);

        // Read the output file
        const data = await ffmpeg.readFile('output.mp3');
        
        // Create a Blob URL for the audio file
        const blob = new Blob([data], { type: 'audio/mpeg' });
        const url = URL.createObjectURL(blob);
        
        setAudioUrl(url);
        setIsLoading(false);
        setProgress(100);
      } catch (err) {
        console.error('Error during audio extraction:', err);
        setError('Failed to extract audio. Please try again with a different file.');
        setIsLoading(false);
      }
    },
    [ffmpeg, isReady]
  );

  return { 
    isReady, 
    isLoading, 
    progress, 
    audioUrl, 
    extractAudio, 
    error 
  };
};