import React from 'react';
import { Music, Upload, FileVideo } from 'lucide-react';
import FileUploader from './components/FileUploader';
import Header from './components/Header';
import Footer from './components/Footer';

function App() {
  return (
    <div className="min-h-screen flex flex-col bg-gradient-to-br from-purple-50 to-indigo-50">
      <Header />
      
      <main className="flex-grow flex items-center justify-center p-4">
        <div className="max-w-4xl w-full">
          <div className="text-center mb-8">
            <h1 className="text-3xl md:text-4xl font-bold text-gray-800 mb-2">
              MP4 to MP3 Converter
            </h1>
            <p className="text-gray-600 max-w-2xl mx-auto">
              Extract audio tracks from your videos with just a few clicks. 
              Upload an MP4 file and download its audio as an MP3.
            </p>
          </div>

          <div className="bg-white rounded-xl shadow-lg overflow-hidden">
            <div className="p-6">
              <div className="flex items-center justify-center gap-3 mb-6">
                <div className="flex items-center justify-center w-10 h-10 rounded-full bg-purple-100 text-purple-600">
                  <FileVideo size={20} />
                </div>
                <div className="h-0.5 w-8 bg-purple-200"></div>
                <div className="flex items-center justify-center w-10 h-10 rounded-full bg-purple-100 text-purple-600">
                  <Upload size={20} />
                </div>
                <div className="h-0.5 w-8 bg-purple-200"></div>
                <div className="flex items-center justify-center w-10 h-10 rounded-full bg-purple-100 text-purple-600">
                  <Music size={20} />
                </div>
              </div>
              
              <FileUploader />
            </div>
          </div>
        </div>
      </main>
      
      <Footer />
    </div>
  );
}

export default App;