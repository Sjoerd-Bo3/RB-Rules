/** @type {import('next').NextConfig} */
const nextConfig = {
  // PWA-ready: manifest + camera/mic require HTTPS in production
  // (gebruik bv. Cloudflare Tunnel bij self-hosting op een Mac mini).
  reactStrictMode: true,
};

export default nextConfig;
