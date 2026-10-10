import { NavLink, Route, Routes } from "react-router-dom";
import Library from "./pages/Library";
import MediaDetail from "./pages/MediaDetail";
import Search from "./pages/Search";
import Requests from "./pages/Requests";
import Jobs from "./pages/Jobs";
import Settings from "./pages/Settings";
import System from "./pages/System";

export default function App() {
  return (
    <div className="app">
      <header>
        <h1>MediaHub</h1>
        <nav>
          <NavLink to="/">Bibliothèque</NavLink>
          <NavLink to="/search">Recherche</NavLink>
          <NavLink to="/requests">Requêtes</NavLink>
          <NavLink to="/jobs">Jobs</NavLink>
          <NavLink to="/settings">Réglages</NavLink>
          <NavLink to="/system">Système</NavLink>
        </nav>
      </header>
      <main>
        <Routes>
          <Route path="/" element={<Library />} />
          <Route path="/media/:id" element={<MediaDetail />} />
          <Route path="/search" element={<Search />} />
          <Route path="/requests" element={<Requests />} />
          <Route path="/jobs" element={<Jobs />} />
          <Route path="/settings" element={<Settings />} />
          <Route path="/system" element={<System />} />
        </Routes>
      </main>
    </div>
  );
}
