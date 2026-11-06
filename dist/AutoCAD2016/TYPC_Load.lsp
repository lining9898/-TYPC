;;; ============================================================
;;; TYPC Toolbox - AutoCAD 2016 loader
;;; DLL  : TYPC_vX.Y.Z.dll (acdbmgd/acmgd 20.1 = AutoCAD 2016)
;;; Note : self-contained - no extra DLL has to sit next to it.
;;;
;;; HOW TO USE
;;;   1) Manual : APPLOAD -> pick this file
;;;               (or drag this file into the drawing area)
;;;   2) Auto   : APPLOAD -> Startup Suite -> Contents... -> add
;;;               this file. It then loads on every drawing.
;;;
;;; The DLL file name carries its version (like TYPC_v1.9.3.dll).
;;; This loader auto-detects the NEWEST TYPC_v*.dll in the folder
;;; below, so it never needs editing after an upgrade.
;;; If you move the folder, edit the line marked EDIT below.
;;; ============================================================

(vl-load-com)

;;; --- EDIT THIS LINE IF YOU MOVE THE DLL FOLDER ---------------
(setq *TYPC-DLL-DIR* "D:\\ai\\TYPC\\dist\\AutoCAD2016\\")
;;; ------------------------------------------------------------

;;; Pick the dll to load, by priority:
;;;   1) newest TYPC_v*.dll inside *TYPC-DLL-DIR*
;;;   2) plain TYPC.dll inside *TYPC-DLL-DIR*
;;;   3) TYPC.dll on the AutoCAD support file search path
(defun TYPC:FindDll (/ files)
  (setq files (vl-directory-files *TYPC-DLL-DIR* "TYPC_v*.dll" 1))
  (cond
    (files
      (strcat *TYPC-DLL-DIR* (car (last (vl-sort files (function <))))))
    ((findfile (strcat *TYPC-DLL-DIR* "TYPC.dll"))
      (findfile (strcat *TYPC-DLL-DIR* "TYPC.dll")))
    ((findfile "TYPC.dll")
      (findfile "TYPC.dll"))
    (T nil)))

(defun TYPC-LOAD (/ dll result)
  (if (vl-bb-ref 'TYPC:LOADED)
    (princ)
    (progn
      (setq dll (TYPC:FindDll))
      (if (null dll)
        (progn
          (princ "\n[TYPC] ERROR - no TYPC_v*.dll found in:")
          (princ (strcat "\n[TYPC]   " *TYPC-DLL-DIR*))
          (princ "\n[TYPC] Fix: edit *TYPC-DLL-DIR* in this .lsp")
          (princ "\n[TYPC]      or run NETLOAD and pick the dll."))
        (progn
          (setq result
            (vl-catch-all-apply 'command (list "_NETLOAD" dll)))
          (if (vl-catch-all-error-p result)
            (progn
              (princ "\n[TYPC] ERROR - NETLOAD was cancelled/failed.")
              (princ "\n[TYPC] If Windows blocked the file:")
              (princ "\n[TYPC] right-click the dll -> Properties")
              (princ "\n[TYPC] -> Unblock, or run NETLOAD manually."))
            (progn
              (vl-bb-set 'TYPC:LOADED T)
              (princ (strcat "\n[TYPC] loaded: " dll))
              (princ "\n[TYPC] TYPC / TYY / THUI  - toolbox panel")
              (princ "\n[TYPC] THBH / THSZ / THCS / THBJ - numbers")
              (princ "\n[TYPC] BBLK / BATCHBLOCK - batch make blocks")
              (princ "\n[TYPC] SELBLK - select same-name blocks")
              (princ "\n[TYPC] ADDBLK - add entities into a block")
              (princ "\n[TYPC] WZXX / TAXX - text & pattern linetype")
              (princ "\n[TYPC] WZQF - text prefix/suffix")
              (princ "\n[TYPC] TYPCV - show version")))))))
  (princ))

;;; Run now (covers APPLOAD / drag-and-drop).
(TYPC-LOAD)

;;; Also hook S::STARTUP so the Startup Suite path works even when
;;; COMMAND is not yet usable at load time.
(defun-q TYPC:STARTUP ()
  (TYPC-LOAD)
  (princ))

(if (and (boundp 'S::STARTUP) (listp S::STARTUP))
  (setq S::STARTUP (append S::STARTUP TYPC:STARTUP))
  (setq S::STARTUP TYPC:STARTUP))

(princ)
