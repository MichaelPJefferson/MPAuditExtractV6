import React from 'react';
import { Headphones } from 'lucide-react';

const Header = () => {
  return (
    <header className="bg-white shadow-sm">
      <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
        <div className="flex justify-between items-center h-16">
          <div className="flex items-center">
            <Headphones className="h-8 w-8 text-purple-600" />
            <span className="ml-2 text-xl font-semibold text-gray-800">AudioExtractor</span>
          </div>
          
          <nav>
            <ul className="flex space-x-4">
              <li>
                <a 
                  href="https://github.com/ffmpegwasm/ffmpeg.wasm" 
                  target="_blank" 
                  rel="noopener noreferrer"
                  className="text-gray-600 hover:text-purple-600 transition-colors duration-200"
                >
                  Powered by ffmpeg.wasm
                </a>
              </li>
            </ul>
          </nav>
        </div>
      </div>
    </header>
  );
};

export default Header;