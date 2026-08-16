import { createRoot } from "react-dom/client";
import "./styles.css";
import { Shell } from "./components/Shell";
import { HomePage } from "./pages/home";
import { InspectorPage } from "./pages/inspector";
import { StudioPage } from "./pages/studio";
import { useRoute } from "./router";

const App = () => {
  const route = useRoute();

  return (
    <Shell page={route.page}>
      {route.page === "inspector" ? (
        <InspectorPage />
      ) : route.page === "studio" ? (
        <StudioPage />
      ) : (
        <HomePage />
      )}
    </Shell>
  );
};

createRoot(document.getElementById("root")!).render(<App />);
