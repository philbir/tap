import { createRoot } from "react-dom/client";
import "./styles.css";
import { Shell } from "./components/Shell";
import { DownloadPage } from "./pages/download";
import { HomePage } from "./pages/home";
import { TunnelsPage } from "./pages/tunnels";
import { StudioPage } from "./pages/studio";
import { useRoute } from "./router";

const App = () => {
  const route = useRoute();

  return (
    <Shell page={route.page}>
      {route.page === "tunnels" ? (
        <TunnelsPage />
      ) : route.page === "studio" ? (
        <StudioPage />
      ) : route.page === "download" ? (
        <DownloadPage />
      ) : (
        <HomePage />
      )}
    </Shell>
  );
};

createRoot(document.getElementById("root")!).render(<App />);
